using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ClipSync.Platform;
using ClipSync.Security;

namespace ClipSync.Platform.Backends.X11;

/// Clipboard access through the X11 selection protocol, over XWayland.
///
/// The primary backend on GNOME, because Mutter advertises no data-control
/// global at all (see HANDOFF.md) and the focus-gated wl_data_device is
/// unusable from a daemon. Mutter bridges its Wayland clipboard to the X
/// selection in both directions, so this sees copies made in Wayland-native
/// apps and its writes are visible to them — both measured, not assumed.
///
/// All Xlib/XCB work happens on one dedicated thread. The X connection is
/// not thread-safe, INCR transfers are a stateful conversation across many
/// events, and XWayland can exit under us — a single owning thread that can
/// tear down and rebuild is far easier to reason about than locking a
/// connection that might already be dead.
internal sealed class X11ClipboardBackend : IClipboardBackend
{
    public string Name => "X11Fixes";

    private readonly ConcurrentQueue<Dictionary<string, byte[]>> _pendingWrites = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private Action<IClipboardOffer>? _onSelection;

    public bool IsAvailable()
    {
        if (Environment.GetEnvironmentVariable("DISPLAY") is null) return false;
        try
        {
            using var probe = XSelection.Connect();
            return true;
        }
        catch (Exception ex)
        {
            Identity.Log($"X11Fixes: unavailable ({ex.Message})");
            return false;
        }
    }

    public void Start(Action<IClipboardOffer> onSelection)
    {
        _onSelection = onSelection;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "clipsync-x11",
        };
        _thread.Start();
    }

    public void SetSelection(IReadOnlyDictionary<string, byte[]> data)
    {
        // Hand the work to the X thread rather than touching the connection
        // from the caller's.
        _pendingWrites.Enqueue(new Dictionary<string, byte[]>(data));
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            XSelection? x = null;
            try
            {
                x = XSelection.Connect();
                x.WatchClipboard();
                Identity.Log("X11Fixes: connected to X, watching CLIPBOARD");

                x.ClipboardChanged += owner => OnClipboardChanged(x!, owner);

                while (!_stop.IsCancellationRequested)
                {
                    // Apply any writes queued since the last pass. Only the
                    // most recent matters: an older one has already been
                    // superseded on the sender's clipboard too.
                    Dictionary<string, byte[]>? write = null;
                    while (_pendingWrites.TryDequeue(out var w)) write = w;
                    if (write is not null)
                    {
                        var owned = x.OwnClipboard(write);
                        Identity.Log($"X11Fixes: took clipboard ownership " +
                                     $"({write.Count} format(s), ok={owned})");
                    }

                    x.Pump(200);
                }
            }
            catch (XConnectionLostException ex)
            {
                // XWayland is started on demand and can exit; this is an
                // ordinary event, not a crash. Rebuild from scratch — the
                // old connection's window, atoms and selection ownership
                // all died with it.
                Identity.Log($"X11Fixes: {ex.Message}, reconnecting in 1s");
            }
            catch (Exception ex)
            {
                Identity.Log($"X11Fixes: loop error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                x?.Dispose();
            }

            if (!_stop.IsCancellationRequested) Thread.Sleep(1000);
        }
    }

    private void OnClipboardChanged(XSelection x, uint owner)
    {
        try
        {
            // Our own write coming back. Reading it would mean converting
            // the selection from ourselves on the one connection that is
            // also serving it: an INCR transfer would then need this thread
            // to answer its own chunk requests while it is blocked waiting
            // for them, so anything over the inline threshold stalls until
            // it times out — and while it stalls, real requestors are not
            // served either. There is nothing to learn from the read anyway;
            // the writer stamped the content before publishing it.
            if (owner == x.Window)
            {
                Identity.Log("X11Fixes: clipboard change is our own write, not re-reading");
                return;
            }

            var targets = x.ReadTargets();
            if (targets.Length == 0) return;

            var source = x.IdentifyOwner(owner, out var how);
            Identity.Log($"X11Fixes: clipboard changed, {targets.Length} target(s), " +
                         $"source={source ?? "<unidentifiable>"} [{how}]");

            _onSelection?.Invoke(new X11Offer(x, targets, source));
        }
        catch (XConnectionLostException)
        {
            throw;                       // let the loop rebuild the connection
        }
        catch (Exception ex)
        {
            Identity.Log($"X11Fixes: reading selection failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(2000);
        _stop.Dispose();
    }

    /// Valid only for the duration of the callback: it reads through the X
    /// thread's live connection, so using it later or from another thread
    /// would race with that thread's own event pumping.
    private sealed class X11Offer : IClipboardOffer
    {
        private readonly XSelection _x;

        public IReadOnlyList<string> MimeTypes { get; }
        public string? SourceApp { get; }

        public X11Offer(XSelection x, IReadOnlyList<string> mimeTypes, string? sourceApp)
        {
            _x = x;
            MimeTypes = mimeTypes;
            SourceApp = sourceApp;
        }

        public byte[]? Receive(string mimeType) => _x.ReadTarget(mimeType);
    }
}
