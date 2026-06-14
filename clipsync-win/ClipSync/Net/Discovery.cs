using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
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
    private int _port;
    private int _restartPending;

    /// All usable addresses a peer advertised, tried in order on connect.
    private sealed record Endpoint(IReadOnlyList<IPAddress> Addresses, int Port, string SrvTarget);

    /// Cache endpoint info so we can connect after the user clicks Trust
    /// and retry from the maintenance loop without waiting for mDNS.
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new();
    private readonly ConcurrentDictionary<string, byte> _connecting = new();

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
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();

        StartMdns();

        // mDNS sockets go stale after sleep/wake or a Wi-Fi switch;
        // rebuild advertise+browse whenever the network changes.
        NetworkChange.NetworkAddressChanged += (_, _) => ScheduleMdnsRestart("address change");
        NetworkChange.NetworkAvailabilityChanged += (_, _) => ScheduleMdnsRestart("availability change");

        _ = MaintenanceLoop();
    }

    private void StartMdns()
    {
        var sd = new ServiceDiscovery();
        var profile = new ServiceProfile(Environment.MachineName, "_clipsync._tcp", (ushort)_port);
        profile.AddProperty("v", "1");
        profile.AddProperty("did", _identity.DidHex);
        profile.AddProperty("name", Environment.MachineName);
        profile.AddProperty("caps", "text,image,files,rich");
        profile.AddProperty("pend", _trust.IsEmpty ? "1" : "0");
        sd.Advertise(profile);
        try { sd.Announce(profile); } catch { /* unsolicited announce is best-effort */ }

        sd.ServiceInstanceDiscovered += (_, e) => OnInstanceDiscovered(e.ServiceInstanceName.ToString(), e.Message);
        sd.QueryServiceInstances("_clipsync._tcp");
        _sd = sd;
        Identity.Log($"Discovery: mDNS started, advertising port {_port}");
    }

    private void ScheduleMdnsRestart(string reason)
    {
        if (Interlocked.Exchange(ref _restartPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));   // debounce bursts of change events
            Interlocked.Exchange(ref _restartPending, 0);
            Identity.Log($"Discovery: restarting mDNS ({reason})");
            try { _sd?.Dispose(); } catch { }
            try { StartMdns(); }
            catch (Exception ex) { Identity.Log($"Discovery: mDNS restart failed: {ex.Message}"); }
        });
    }

    private void OnInstanceDiscovered(string svcName, Message msg)
    {
        if (!svcName.Contains("_clipsync._tcp")) return;

        var didHex = TryReadProperty(msg, "did");
        if (didHex is null) return;
        var key = didHex.ToLowerInvariant();
        if (key == _identity.DidHex) return;   // self

        var name = TryReadProperty(msg, "name") ?? svcName;

        // Responders may put SRV/TXT/A/AAAA in either section.
        var records = msg.Answers.Concat(msg.AdditionalRecords).ToList();
        var srv = records.OfType<SRVRecord>().FirstOrDefault();
        if (srv is not null)
        {
            var addrs = CollectAddresses(records);
            if (addrs.Count > 0 || !_endpoints.ContainsKey(key))
                _endpoints[key] = new Endpoint(addrs, srv.Port, srv.Target.ToString());
        }

        Identity.Log($"Discovery: peer name={name} did={key} port={srv?.Port} " +
                     $"addrs=[{string.Join(",", _endpoints.TryGetValue(key, out var ep) ? ep.Addresses : Array.Empty<IPAddress>())}]");

        // Always register so the UI can show Trust buttons.
        _peers.OnDiscovered(key, name, _trust.Contains(key));

        if (_trust.Contains(key))
            TryConnect(key);
    }

    /// Called after the user clicks Trust — try to connect using cached endpoint.
    public void ConnectToPeer(string didHex)
    {
        TryConnect(didHex.ToLowerInvariant());
        try { _sd?.QueryServiceInstances("_clipsync._tcp"); } catch { }
    }

    private void TryConnect(string key)
    {
        if (_peers.IsConnected(key)) return;
        if (!_endpoints.TryGetValue(key, out var ep))
        {
            Identity.Log($"TryConnect: no cached endpoint for {key}");
            return;
        }
        if (!_connecting.TryAdd(key, 0)) return;   // already attempting
        _ = ConnectAsync(key, ep);
    }

    /// Keep IPv4 (excluding loopback/APIPA) and routable IPv6. Link-local
    /// IPv6 is skipped: mDNS gives no scope ID, so it isn't connectable.
    private static List<IPAddress> CollectAddresses(IEnumerable<ResourceRecord> records)
    {
        var v4 = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        foreach (var rr in records)
        {
            switch (rr)
            {
                case ARecord a when !IPAddress.IsLoopback(a.Address):
                    var b = a.Address.GetAddressBytes();
                    if (!(b[0] == 169 && b[1] == 254)) v4.Add(a.Address);
                    break;
                case AAAARecord aaaa when !IPAddress.IsLoopback(aaaa.Address) && !aaaa.Address.IsIPv6LinkLocal:
                    v6.Add(aaaa.Address);
                    break;
            }
        }
        return v4.Concat(v6).Distinct().ToList();
    }

    private static string? TryReadProperty(Message msg, string key)
    {
        foreach (var rr in msg.Answers.Concat(msg.AdditionalRecords))
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

    /// Periodically retry trusted peers that aren't connected (failed
    /// connects, dropped links, peers that woke from sleep) and re-query
    /// mDNS so the endpoint cache stays fresh.
    private async Task MaintenanceLoop()
    {
        var tick = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            tick++;
            try
            {
                foreach (var entry in _trust.All())
                {
                    var key = entry.DidHex.ToLowerInvariant();
                    if (!_peers.IsConnected(key)) TryConnect(key);
                }
                if (tick % 2 == 0) _sd?.QueryServiceInstances("_clipsync._tcp");
            }
            catch (Exception ex) { Identity.Log($"Discovery: maintenance error: {ex.Message}"); }
        }
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

    private async Task ConnectAsync(string key, Endpoint ep)
    {
        try
        {
            var addrs = new List<IPAddress>(ep.Addresses);
            if (addrs.Count == 0 && ep.SrvTarget.Length > 0)
            {
                // No A/AAAA records came with the mDNS response; Windows
                // resolves .local names via the OS mDNS responder.
                try
                {
                    var resolved = await Dns.GetHostAddressesAsync(ep.SrvTarget.TrimEnd('.'));
                    foreach (var a in resolved)
                    {
                        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal) continue;
                        addrs.Add(a);
                    }
                }
                catch (Exception ex)
                {
                    Identity.Log($"ConnectAsync: resolving {ep.SrvTarget} failed: {ex.Message}");
                }
            }

            foreach (var addr in addrs)
            {
                if (_peers.IsConnected(key)) return;
                TcpClient? c = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    c = new TcpClient(addr.AddressFamily);
                    await c.ConnectAsync(addr, ep.Port, cts.Token);
                    var pc = new PeerConnection(c, _identity, _trust, PeerRole.Client);
                    _peers.Adopt(pc);
                    pc.Start();
                    return;
                }
                catch (Exception ex)
                {
                    c?.Dispose();
                    Identity.Log($"ConnectAsync: {addr}:{ep.Port} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            _connecting.TryRemove(key, out _);
        }
    }
}
