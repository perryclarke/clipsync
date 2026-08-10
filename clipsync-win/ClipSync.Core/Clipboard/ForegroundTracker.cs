using System;
using System.Runtime.InteropServices;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Watches foreground changes and remembers a short history of them.
///
/// Uses SetWinEventHook rather than polling: the OS calls us only when
/// focus actually moves. Must be started on the UI thread, because
/// WINEVENT_OUTOFCONTEXT callbacks are delivered on the message loop of
/// the thread that registered the hook, and UnhookWinEvent must run on
/// that same thread.
public sealed class ForegroundTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly ForegroundRing _ring = new();
    private readonly IWindowResolver _resolver;

    private IntPtr _hook;
    // Held in a field on purpose: the delegate is passed to unmanaged
    // code, and if it is collected the next callback tears down the app.
    private WinEventProc? _callback;

    public ForegroundTracker() : this(new Win32WindowResolver()) { }

    public ForegroundTracker(IWindowResolver resolver) => _resolver = resolver;

    public void Start()
    {
        // Re-entry guard: a second call would overwrite _hook (leaking the
        // first registration) and _callback (unrooting the first hook's
        // still-live delegate) out from under a callback the OS can still
        // fire at any moment.
        if (_hook != IntPtr.Zero) return;

        // Seed with whatever is in front now, so the first copy after
        // launch resolves without waiting for a focus change.
        try { _ring.Record(DateTime.UtcNow, _resolver.Resolve(_resolver.GetForegroundWindow())); }
        catch (Exception ex) { Security.Log.Write($"ForegroundTracker: seed failed: {ex.GetType().Name}"); }

        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                IntPtr.Zero, _callback, 0, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
        {
            _callback = null;
            Security.Log.Write("ForegroundTracker: SetWinEventHook failed; app exclusions will not be enforced");
        }
        else
        {
            Security.Log.Write("ForegroundTracker: started");
        }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;

        // UnhookWinEvent fails when called from a thread other than the one
        // that registered the hook. Only clear the fields when it actually
        // succeeded: if it didn't, the hook is still live in the OS and
        // _callback must stay rooted or the next callback is an access
        // violation.
        if (UnhookWinEvent(_hook))
        {
            _hook = IntPtr.Zero;
            _callback = null;
        }
        else
        {
            Security.Log.Write("ForegroundTracker: UnhookWinEvent failed; hook is still live");
        }
    }

    public void Dispose() => Stop();

    public AppIdentity? AppAt(DateTime utc) => _ring.AppAt(utc);

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd,
                                     int idObject, int idChild, uint thread, uint time)
    {
        // This runs on a callback from the OS message pump, across a native
        // frame: an escaping exception here kills the process rather than
        // faulting catchably, so the whole body is one try/catch.
        try
        {
            // Record the timestamp before resolving: resolution can take a
            // moment and the transition happened now, not when we finished.
            var at = DateTime.UtcNow;
            AppIdentity? app;
            try { app = _resolver.Resolve(hwnd); }
            catch (Exception ex)
            {
                Security.Log.Write($"ForegroundTracker: resolve failed: {ex.GetType().Name}");
                app = null;
            }
            _ring.Record(at, app);
        }
        catch (Exception ex)
        {
            Security.Log.Write($"ForegroundTracker: OnForegroundChanged failed: {ex.GetType().Name}");
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd,
                                       int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
                                                 WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
