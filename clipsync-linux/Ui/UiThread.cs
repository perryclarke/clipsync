using System;
using System.Collections.Generic;
using System.Threading;
using ClipSync.Security;

namespace ClipSync.Ui;

/// Owns the GTK thread.
///
/// The daemon may start before any display exists, and must keep syncing
/// when none ever appears, so GTK is not initialised at startup: the
/// thread starts lazily on the first Post, and refuses to start at all
/// without a display. Everything GTK happens on this one thread; Post is
/// the only way in.
internal sealed class UiThread
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private SynchronizationContext? _context;
    private bool _started;
    private bool _dead;

    /// Valid on the UI thread once the application has activated, which is
    /// before any posted action runs.
    public Adw.Application? Application { get; private set; }

    public void Post(Action action)
    {
        SynchronizationContext context;
        lock (_gate)
        {
            if (_dead) return;
            if (_context is null)
            {
                _pending.Enqueue(action);
                StartLocked();
                return;
            }
            context = _context;
        }
        context.Post(_ => Run(action), null);
    }

    private void StartLocked()
    {
        if (_started) return;
        _started = true;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Identity.Log("Ui: no DISPLAY or WAYLAND_DISPLAY — the window is unavailable");
            _dead = true;
            _pending.Clear();
            return;
        }

        new Thread(ThreadMain) { Name = "clipsync-ui", IsBackground = true }.Start();
    }

    private void ThreadMain()
    {
        try
        {
            // NonUnique matters: with the default flags GApplication is
            // single-instance per app id, and a second daemon's Run would
            // silently activate the first one's window instead of its own.
            var app = Adw.Application.New("org.clipsync.ClipSync",
                                          Gio.ApplicationFlags.NonUnique);
            Application = app;
            app.OnActivate += (_, _) =>
            {
                // With hide-on-close there are often zero live windows;
                // without a hold GApplication would quit the loop then.
                app.Hold();

                List<Action> drained;
                lock (_gate)
                {
                    // Installed by RunWithSynchronizationContext; capturing
                    // it here is what makes Post thread-safe from now on.
                    _context = SynchronizationContext.Current
                        ?? throw new InvalidOperationException("no GLib synchronization context");
                    drained = new List<Action>(_pending);
                    _pending.Clear();
                }
                foreach (var action in drained) Run(action);
            };
            app.RunWithSynchronizationContext(null);
            Identity.Log("Ui: GTK main loop exited");
        }
        catch (Exception ex)
        {
            Identity.Log("Ui: GTK thread failed — the window is unavailable");
            // The chain matters: a TypeInitializationException's outer
            // message says nothing.
            for (var e = ex; e is not null; e = e.InnerException)
                Identity.Log($"Ui:   {e.GetType().Name}: {e.Message}");
        }
        lock (_gate)
        {
            _dead = true;
            _context = null;
            _pending.Clear();
        }
    }

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Identity.Log($"Ui: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
