using System;
using System.Threading.Tasks;
using ClipSync.Security;
using Tmds.DBus.Protocol;

namespace ClipSync.Platform;

/// The single-instance guard, as ownership of a well-known bus name.
///
/// A name request is atomic and the bus releases the name when the owner
/// dies, so unlike a pidfile there is no stale entry to second-guess.
/// Losing the race is not a failure: it means a daemon is already running,
/// and the launch becomes "open that one's window" instead.
///
/// This sits above GTK deliberately. Adw.Application stays NonUnique (see
/// UiThread) — the daemon that owns the name owns its own window, and no
/// second daemon gets far enough to have one.
internal sealed class SingleInstance : IAsyncDisposable, IPathMethodHandler
{
    public const string BusName = "org.clipsync.ClipSync";
    private const string ObjectPath = "/org/clipsync/ClipSync";
    private const string Iface = "org.clipsync.ClipSync";

    private DBusConnection? _conn;

    /// Wired after the window exists; a call arriving before then is
    /// answered and dropped rather than queued.
    public Action? OnOpen { get; set; }

    public string Path => ObjectPath;

    // One object, no children: the guard is a single method on a single
    // path, unlike the tray, which fields calls for a subtree.
    public bool HandlesChildPaths => false;

    private async Task<DBusConnection?> ConnectAsync()
    {
        if (_conn is { } existing) return existing;
        _conn = new DBusConnection(DBusAddress.Session
            ?? throw new InvalidOperationException("no session D-Bus address"));
        await _conn.ConnectAsync();
        return _conn;
    }

    /// True if we own the name and are the daemon for this session.
    public async Task<bool> TryAcquireAsync()
    {
        try
        {
            var conn = await ConnectAsync() ?? throw new InvalidOperationException("no connection");
            conn.AddMethodHandler(this);
            return await conn.TryRequestNameAsync(BusName, RequestNameOptions.None);
        }
        catch (Exception ex)
        {
            // No session bus to arbitrate with. Same posture as a missing
            // tray host: the daemon still syncs, it is simply unguarded.
            Identity.Log($"SingleInstance: no session bus ({ex.GetType().Name}: {ex.Message})");
            return true;
        }
    }

    /// Ask the daemon that holds the name to show its window, and report
    /// whether anyone answered.
    ///
    /// This doubles as the probe the backgrounding path uses before it
    /// spawns: nothing on the bus provides this name on demand, so a call
    /// that succeeds means a live daemon took it.
    public async Task<bool> TryOpenRunningAsync()
    {
        try
        {
            var conn = await ConnectAsync();
            if (conn is null) return false;
            MessageBuffer Build()
            {
                var w = conn.GetMessageWriter();
                try
                {
                    w.WriteMethodCallHeader(BusName, ObjectPath, Iface, "Open");
                    return w.CreateMessage();
                }
                finally { w.Dispose(); }
            }
            await conn.CallMethodAsync(Build());
            return true;
        }
        catch (Exception ex)
        {
            Identity.Log($"SingleInstance: Open failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        if (request.InterfaceAsString == Iface && request.MemberAsString == "Open")
        {
            // Reply before opening: the caller is blocked on the method
            // return and building the window is not instant.
            context.Reply(context.CreateReplyWriter("").CreateMessage());
            Identity.Log("SingleInstance: <- Open");
            OnOpen?.Invoke();
        }
        else
        {
            context.ReplyUnknownMethodError();
        }
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is { } conn)
        {
            try { await conn.ReleaseNameAsync(BusName); } catch { }
            conn.Dispose();
        }
        _conn = null;
    }
}
