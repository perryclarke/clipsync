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
    private MulticastService? _mdns;
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
        // Pin mDNS to the physical LAN NIC(s). Makaretu otherwise follows the
        // OS default multicast route, which a VPN (e.g. NordVPN's tunnel, a
        // lower-metric interface) captures — sending our queries into the
        // tunnel so LAN peers are never discovered. The filter drops tunnel /
        // VPN adapters so discovery keeps working with a VPN active.
        var mdns = new MulticastService(SelectInterfaces);
        var sd = new ServiceDiscovery(mdns);
        var profile = new ServiceProfile(Environment.MachineName, "_clipsync._tcp", (ushort)_port);
        profile.AddProperty("v", "1");
        profile.AddProperty("did", _identity.DidHex);
        profile.AddProperty("name", Environment.MachineName);
        profile.AddProperty("caps", "text,image,files,rich");
        profile.AddProperty("pend", _trust.IsEmpty ? "1" : "0");

        sd.ServiceInstanceDiscovered += (_, e) => OnInstanceDiscovered(e.ServiceInstanceName.ToString(), e.Message);
        mdns.Start();
        sd.Advertise(profile);
        try { sd.Announce(profile); } catch { /* unsolicited announce is best-effort */ }
        sd.QueryServiceInstances("_clipsync._tcp");
        _mdns = mdns;
        _sd = sd;
        Identity.Log($"Discovery: mDNS started, advertising port {_port}");
    }

    /// Choose which NICs Makaretu multicasts on. Given the OS candidate set,
    /// keep real LAN interfaces and drop tunnel/VPN adapters so a VPN with a
    /// lower interface metric can't hijack mDNS. Every candidate is logged so
    /// we can see (and refine) the choice from the debug log.
    private static IEnumerable<NetworkInterface> SelectInterfaces(IEnumerable<NetworkInterface> nics)
    {
        var all = nics.ToList();
        foreach (var ni in all)
            Identity.Log($"Discovery: NIC '{ni.Name}' desc='{ni.Description}' " +
                         $"type={ni.NetworkInterfaceType} status={ni.OperationalStatus} mcast={ni.SupportsMulticast}");

        var kept = all.Where(ni =>
            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
            !LooksLikeVpn(ni)).ToList();

        // Never hand back an empty set — that would disable mDNS entirely.
        // Fall back to whatever the OS offered if the filter removed everything.
        var result = kept.Count > 0 ? kept : all;
        Identity.Log($"Discovery: mDNS bound to [{string.Join(", ", result.Select(k => k.Name))}]");
        return result;
    }

    private static bool LooksLikeVpn(NetworkInterface ni)
    {
        var s = (ni.Name + " " + ni.Description).ToLowerInvariant();
        return s.Contains("nord") || s.Contains("vpn") || s.Contains("tunnel")
            || s.Contains("wireguard") || s.Contains("openvpn")
            || s.Contains("tap-") || s.Contains("wintun");
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
            try { _mdns?.Stop(); } catch { }
            try { _mdns?.Dispose(); } catch { }
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
        // A single mDNS packet can bundle records for unrelated services
        // (e.g. the peer's SMB share on port 445) and for other hosts. Pick
        // the SRV that actually belongs to THIS _clipsync._tcp instance, and
        // only take addresses advertised for that SRV's target host —
        // otherwise a stray SMB SRV / a peer's VPN address overwrites the
        // real clipsync endpoint and we dial the wrong port/host.
        var srv = records.OfType<SRVRecord>()
            .FirstOrDefault(r => DnsEquals(r.Name.ToString(), svcName));
        if (srv is not null)
        {
            var addrs = CollectAddresses(records, srv.Target.ToString());
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

    /// Collect the addresses advertised for a specific host (the SRV target).
    /// Keep IPv4 (excluding loopback/APIPA/unspecified) and routable IPv6.
    /// Link-local IPv6 is skipped: mDNS gives no scope ID, so it isn't
    /// connectable. Addresses belonging to other hosts in the same packet are
    /// ignored, and LAN-subnet addresses are tried before off-subnet ones (so
    /// a peer's VPN address doesn't burn the connect timeout first).
    private static List<IPAddress> CollectAddresses(IEnumerable<ResourceRecord> records, string targetHost)
    {
        var target = NormalizeDnsName(targetHost);
        var v4 = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        foreach (var rr in records)
        {
            switch (rr)
            {
                case ARecord a when NormalizeDnsName(a.Name.ToString()) == target
                                    && !IPAddress.IsLoopback(a.Address)
                                    && !a.Address.Equals(IPAddress.Any):
                    var b = a.Address.GetAddressBytes();
                    if (!(b[0] == 169 && b[1] == 254)) v4.Add(a.Address);
                    break;
                case AAAARecord aaaa when NormalizeDnsName(aaaa.Name.ToString()) == target
                                    && !IPAddress.IsLoopback(aaaa.Address)
                                    && !aaaa.Address.IsIPv6LinkLocal
                                    && !aaaa.Address.Equals(IPAddress.IPv6Any):
                    v6.Add(aaaa.Address);
                    break;
            }
        }
        return v4.Concat(v6).Distinct()
                 .OrderByDescending(IsOnLocalSubnet)   // stable: LAN addresses first
                 .ToList();
    }

    private static string NormalizeDnsName(string name) => name.TrimEnd('.').ToLowerInvariant();

    private static bool DnsEquals(string a, string b) => NormalizeDnsName(a) == NormalizeDnsName(b);

    /// True if addr sits on a subnet one of our up interfaces is on.
    private static bool IsOnLocalSubnet(IPAddress addr)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != addr.AddressFamily) continue;
                if (SameSubnet(addr, ua.Address, ua.PrefixLength)) return true;
            }
        }
        return false;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        var ba = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        if (ba.Length != bb.Length || prefixLength < 0 || prefixLength > ba.Length * 8) return false;
        int fullBytes = prefixLength / 8, remBits = prefixLength % 8;
        for (int i = 0; i < fullBytes; i++)
            if (ba[i] != bb[i]) return false;
        if (remBits == 0) return true;
        int mask = 0xFF << (8 - remBits) & 0xFF;
        return (ba[fullBytes] & mask) == (bb[fullBytes] & mask);
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
