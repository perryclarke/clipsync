using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ClipSync.Platform;
using ClipSync.Security;
using Tmds.DBus.Protocol;

namespace ClipSync.Platform.Backends;

/// mDNS through the system avahi-daemon, over its D-Bus API.
///
/// Deliberately not an in-process mDNS responder. avahi-daemon already owns
/// UDP 5353 on a normal Linux desktop, and a second responder on the same
/// host means two devices answering for one machine — intermittent, and
/// miserable to debug. Delegating also means Avahi handles the parts the
/// Windows client had to work around by hand: interface selection, IPv6
/// link-local scoping, and re-announcing after a network change.
internal sealed class AvahiDBus : IDiscoveryBackend
{
    private const string Svc = "org.freedesktop.Avahi";
    private const string ServerIface = $"{Svc}.Server";
    private const string EntryGroupIface = $"{Svc}.EntryGroup";
    private const string BrowserIface = $"{Svc}.ServiceBrowser";

    /// Avahi's "any interface" / "any protocol" sentinels.
    private const int IfUnspec = -1;
    private const int ProtoUnspec = -1;
    private const int ProtoInet6 = 1;

    public const string ServiceType = "_clipsync._tcp";

    public string Name => "AvahiDBus";

    private DBusConnection? _conn;
    private string? _group;

    /// The signal subscription. Held deliberately: WatchSignalAsync returns
    /// the observer's lifetime as an IDisposable, and dropping it on the
    /// floor lets a garbage collection quietly cancel the subscription —
    /// browsing then simply stops, with no error anywhere.
    private IDisposable? _browseSubscription;

    private delegate void ArgWriter(ref MessageWriter w);

    /// One ItemNew announcement. Named rather than a tuple so the signal
    /// handler's delegate type stays legible at the call site.
    private readonly record struct BrowseItem(
        int Iface, int Proto, string Name, string Type, string Domain);

    // ---- connection -------------------------------------------------

    private async Task<DBusConnection> ConnAsync()
    {
        if (_conn is { } existing) return existing;
        var conn = new DBusConnection(DBusAddress.System
            ?? throw new InvalidOperationException("no system D-Bus address"));
        await conn.ConnectAsync();
        _conn = conn;
        return conn;
    }

    /// MessageWriter is a ref struct and cannot survive an await, so the
    /// message is built in a synchronous helper and only then sent.
    private MessageBuffer Build(DBusConnection conn, string path, string iface,
                                string member, string signature, ArgWriter? args)
    {
        var w = conn.GetMessageWriter();
        try
        {
            w.WriteMethodCallHeader(Svc, path, iface, member, signature);
            args?.Invoke(ref w);
            return w.CreateMessage();
        }
        finally { w.Dispose(); }
    }

    private async Task<T> CallAsync<T>(string path, string iface, string member,
                                       string signature, ArgWriter? args,
                                       MessageValueReader<T> read)
    {
        var conn = await ConnAsync();
        return await conn.CallMethodAsync(Build(conn, path, iface, member, signature, args), read, null);
    }

    private Task CallAsync(string path, string iface, string member,
                           string signature, ArgWriter? args)
        => CallAsync<object?>(path, iface, member, signature, args, (Message _, object? _) => null);

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var version = await CallAsync("/", ServerIface, "GetVersionString", "", null,
                (Message m, object? _) => m.GetBodyReader().ReadString());
            Identity.Log($"AvahiDBus: {version}");
            return true;
        }
        catch (Exception ex)
        {
            Identity.Log($"AvahiDBus: unavailable ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    // ---- advertise --------------------------------------------------

    public async Task PublishAsync(string serviceName, int port,
                                   IReadOnlyDictionary<string, string> txt)
    {
        // Avahi rejects a service name already claimed on this host or
        // subnet, and the name is cosmetic — the did in the TXT record is
        // the real identity — so rename rather than fail. This happens for
        // real when two machines share a hostname, and during development
        // whenever an old instance has not finished shutting down.
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? serviceName : $"{serviceName} #{attempt}";
            try
            {
                await PublishOnceAsync(candidate, port, txt);
                if (attempt > 1)
                    Identity.Log($"AvahiDBus: name '{serviceName}' was taken, published as '{candidate}'");
                return;
            }
            catch (DBusErrorReplyException ex) when (
                ex.ErrorName == "org.freedesktop.Avahi.CollisionError" && attempt < 8)
            {
                Identity.Log($"AvahiDBus: '{candidate}' collided, retrying");
            }
        }
    }

    private async Task PublishOnceAsync(string serviceName, int port,
                                        IReadOnlyDictionary<string, string> txt)
    {
        // Reset rather than create a second group: republishing is how the
        // `pend` flag flips once the trust store stops being empty, and two
        // live groups would advertise the old and new TXT at once.
        if (_group is { } existing)
        {
            try { await CallAsync(existing, EntryGroupIface, "Reset", "", null); }
            catch (Exception ex) { Identity.Log($"AvahiDBus: group reset failed: {ex.Message}"); }
        }
        else
        {
            _group = await CallAsync("/", ServerIface, "EntryGroupNew", "", null,
                (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString());
        }

        var entries = new List<byte[]>();
        foreach (var kv in txt) entries.Add(Encoding.UTF8.GetBytes($"{kv.Key}={kv.Value}"));

        await CallAsync(_group!, EntryGroupIface, "AddService", "iiussssqaay", (ref MessageWriter w) =>
        {
            w.WriteInt32(IfUnspec);
            w.WriteInt32(ProtoUnspec);
            w.WriteUInt32(0);                 // flags
            w.WriteString(serviceName);
            w.WriteString(ServiceType);
            w.WriteString("");                // domain: default (.local)
            w.WriteString("");                // host: this machine
            w.WriteUInt16((ushort)port);
            var a = w.WriteArrayStart(DBusType.Array);
            foreach (var e in entries) w.WriteArray(e);
            w.WriteArrayEnd(a);
        });

        await CallAsync(_group!, EntryGroupIface, "Commit", "", null);
        Identity.Log($"AvahiDBus: published {serviceName} {ServiceType} port {port} " +
                     $"txt=[{string.Join(",", txt.Keys)}]");
    }

    // ---- browse -----------------------------------------------------

    public async Task BrowseAsync(Action<DiscoveredService> onFound)
    {
        var conn = await ConnAsync();

        void OnItemNew(Notification<BrowseItem> n)
        {
            // Order matters: Notification<T>.Exception THROWS unless the
            // notification is a completion, so it cannot be used as a
            // first-line "did this fail?" check. Getting this backwards
            // throws inside the observer callback on the very first signal,
            // which tears the subscription down without surfacing anything —
            // browsing just silently never reports a peer.
            if (!n.HasValue)
            {
                var reason = n.IsCompletion && n.Exception is { } e ? $": {e.Message}" : "";
                Identity.Log($"AvahiDBus: browse ended ({n.Type}){reason}");
                return;
            }
            var item = n.Value;
            // Resolve off the signal callback: resolving is itself a method
            // call, and blocking here would stall the browser.
            _ = ResolveAsync(item.Iface, item.Proto, item.Name, item.Type, item.Domain, onFound);
        }

        // Subscribe BEFORE creating the browser, and match on any object
        // path rather than the browser's own.
        //
        // Avahi emits ItemNew for every service it already knows the moment
        // ServiceBrowserNew returns. Creating the browser first and then
        // subscribing — the obvious order, since that call is what yields
        // the path to match on — loses that entire initial burst, so every
        // peer that was already on the network stays invisible while newly
        // announced ones appear normally. This connection has exactly one
        // browser, so a null path is unambiguous.
        //
        // The cast picks the synchronous Action overload; without it the
        // lambda binds to the Func<_, ValueTask> one.
        _browseSubscription = await conn.WatchSignalAsync(Svc, null, BrowserIface, "ItemNew",
            (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                return new BrowseItem(r.ReadInt32(), r.ReadInt32(), r.ReadString(),
                                      r.ReadString(), r.ReadString());
            },
            (Action<Notification<BrowseItem>>)OnItemNew, ObserverFlags.None, false, null);

        var browser = await CallAsync("/", ServerIface, "ServiceBrowserNew", "iissu", (ref MessageWriter w) =>
        {
            w.WriteInt32(IfUnspec);
            w.WriteInt32(ProtoUnspec);
            w.WriteString(ServiceType);
            w.WriteString("");
            w.WriteUInt32(0);
        }, (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString());

        Identity.Log($"AvahiDBus: browsing {ServiceType} via {browser}");
    }

    private async Task ResolveAsync(int iface, int proto, string name, string type,
                                    string domain, Action<DiscoveredService> onFound)
    {
        try
        {
            var result = await CallAsync("/", ServerIface, "ResolveService", "iisssiu",
                (ref MessageWriter w) =>
                {
                    w.WriteInt32(iface);
                    w.WriteInt32(proto);
                    w.WriteString(name);
                    w.WriteString(type);
                    w.WriteString(domain);
                    w.WriteInt32(ProtoUnspec);   // accept whichever address family resolves
                    w.WriteUInt32(0);
                },
                (Message m, object? _) =>
                {
                    var r = m.GetBodyReader();
                    var rIface = r.ReadInt32();
                    var rProto = r.ReadInt32();
                    r.ReadString();                  // name
                    r.ReadString();                  // type
                    r.ReadString();                  // domain
                    var host = r.ReadString();
                    var aproto = r.ReadInt32();
                    var address = r.ReadString();
                    var port = (int)r.ReadUInt16();

                    var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var end = r.ReadArrayStart(DBusType.Array);
                    while (r.HasNext(end))
                    {
                        var entry = Encoding.UTF8.GetString(r.ReadArrayOfByte());
                        var eq = entry.IndexOf('=');
                        if (eq > 0) txt[entry[..eq]] = entry[(eq + 1)..];
                    }
                    return (rIface, rProto, host, aproto, address, port, txt);
                });

            if (!result.txt.TryGetValue("did", out var did) || did.Length == 0) return;
            if (!IPAddress.TryParse(result.address, out var ip)) return;

            // A link-local address is ambiguous without the interface it was
            // seen on; connecting to one without a scope id fails with
            // "invalid argument". Avahi tells us the interface index, so use it.
            if (ip.IsIPv6LinkLocal && result.rIface >= 0) ip.ScopeId = result.rIface;

            var peerName = result.txt.TryGetValue("name", out var n) && n.Length > 0
                ? n : result.host.TrimEnd('.');

            onFound(new DiscoveredService(did.ToLowerInvariant(), peerName, ip, result.port, result.txt));
        }
        catch (Exception ex)
        {
            // A peer that vanishes between announcement and resolve is
            // ordinary, not an error worth surfacing.
            Identity.Log($"AvahiDBus: resolve {name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_group is { } g && _conn is not null)
        {
            try { await CallAsync(g, EntryGroupIface, "Reset", "", null); } catch { }
        }
        _browseSubscription?.Dispose();
        _browseSubscription = null;
        _conn?.Dispose();
        _conn = null;
        _group = null;
    }
}
