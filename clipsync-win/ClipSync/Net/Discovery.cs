using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Makaretu.Dns;
using ClipSync.Security;

namespace ClipSync.Net;

/// mDNS advertise + browse for `_clipsync._tcp` over IPv6, plus an
/// IPv6 TCP listener that PeerConnection drives.
public sealed class Discovery
{
    private readonly Identity _identity;
    private readonly TrustStore _trust;
    private readonly PeerRegistry _peers;
    private ServiceDiscovery? _sd;
    private TcpListener? _listener;

    /// Cache endpoint info so we can connect after the user clicks Trust.
    private readonly ConcurrentDictionary<string, (IPAddress Addr, int Port)> _endpoints = new();

    public Discovery(Identity identity, TrustStore trust, PeerRegistry peers)
    {
        _identity = identity;
        _trust = trust;
        _peers = peers;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();

        _sd = new ServiceDiscovery();
        var profile = new ServiceProfile(Environment.MachineName, "_clipsync._tcp", (ushort)port);
        profile.AddProperty("v", "1");
        profile.AddProperty("did", _identity.DidHex);
        profile.AddProperty("name", Environment.MachineName);
        profile.AddProperty("caps", "text,image,files,rich");
        profile.AddProperty("pend", _trust.IsEmpty ? "1" : "0");
        _sd.Advertise(profile);

        _sd.ServiceInstanceDiscovered += (_, e) =>
        {
            if (e.ServiceInstanceName.ToString().StartsWith(Environment.MachineName)) return;

            var didHex = TryReadProperty(e.Message, "did");
            var name = TryReadProperty(e.Message, "name") ?? e.ServiceInstanceName.ToString();

            if (didHex is null) return;
            var key = didHex.ToLowerInvariant();

            // Cache the endpoint for later Trust-then-connect.
            var srv = FindSrv(e.Message);
            IPAddress? addr = null;
            foreach (var rr in e.Message.AdditionalRecords)
            {
                if (rr is AAAARecord aaaa) { addr = aaaa.Address; break; }
                if (rr is ARecord a) { addr = a.Address; break; }
            }
            if (srv is not null && addr is not null)
                _endpoints[key] = (addr, srv.Port);

            // Always register so the UI can show Trust buttons.
            _peers.OnDiscovered(key, name, _trust.Contains(key));

            if (!_trust.Contains(key)) return;
            if (_peers.IsConnected(key)) return;
            if (addr is not null && srv is not null)
                _ = ConnectAsync(addr, srv.Port);
        };
        _sd.QueryServiceInstances("_clipsync._tcp");
    }

    /// Called after the user clicks Trust — try to connect using cached endpoint.
    public void ConnectToPeer(string didHex)
    {
        var key = didHex.ToLowerInvariant();
        if (_peers.IsConnected(key)) return;
        if (_endpoints.TryGetValue(key, out var ep))
            _ = ConnectAsync(ep.Addr, ep.Port);
        // Also re-query in case the cache is stale.
        _sd?.QueryServiceInstances("_clipsync._tcp");
    }

    private static SRVRecord? FindSrv(Message msg)
    {
        foreach (var rr in msg.AdditionalRecords)
            if (rr is SRVRecord srv) return srv;
        return null;
    }

    private static string? TryReadProperty(Message msg, string key)
    {
        foreach (var rr in msg.AdditionalRecords)
        {
            if (rr is TXTRecord txt)
            {
                foreach (var s in txt.Strings)
                {
                    var idx = s.IndexOf('=');
                    if (idx > 0 && s.Substring(0, idx) == key) return s.Substring(idx + 1);
                }
            }
        }
        return null;
    }

    private async Task AcceptLoop()
    {
        while (_listener is { } l)
        {
            TcpClient? c;
            try { c = await l.AcceptTcpClientAsync(); } catch { break; }
            _ = Task.Run(() =>
            {
                var pc = new PeerConnection(c, _identity, _trust, PeerRole.Server);
                _peers.Adopt(pc);
                pc.Start();
            });
        }
    }

    private async Task ConnectAsync(IPAddress addr, int port)
    {
        try
        {
            var c = new TcpClient(addr.AddressFamily);
            await c.ConnectAsync(addr, port);
            var pc = new PeerConnection(c, _identity, _trust, PeerRole.Client);
            _peers.Adopt(pc);
            pc.Start();
        }
        catch { /* will retry on next discovery */ }
    }
}
