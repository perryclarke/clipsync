using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClipSync.Platform;
using ClipSync.Platform.Backends;
using ClipSync.Security;

namespace ClipSync.Net;

/// Advertise, browse, listen and dial — the Linux counterpart of the
/// Windows Discovery, feeding the same PeerRegistry.
///
/// Much smaller than its Windows sibling because avahi-daemon does the mDNS
/// work: the interface pinning, multicast egress forcing and re-announce
/// bursts that file needs are Avahi's problem here, not ours.
public sealed class Discovery : IAsyncDisposable
{
    private readonly Identity _identity;
    private readonly TrustStore _trust;
    private readonly PeerRegistry _peers;
    private readonly IDiscoveryBackend _backend;

    private TcpListener? _listener;
    private int _port;

    /// Every address a peer has been resolved at, newest last, deduped.
    /// Cached so a Trust click can dial immediately without waiting for the
    /// next announcement, and so the maintenance loop can retry.
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new();
    private readonly ConcurrentDictionary<string, byte> _connecting = new();

    /// What was last advertised in the `pend` TXT field, so republishing
    /// happens only when it actually changes.
    private bool _publishedPending;

    private sealed record Endpoint(List<IPEndPoint> Addresses);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(15);

    public Discovery(Identity identity, TrustStore trust, PeerRegistry peers,
                     IDiscoveryBackend? backend = null)
    {
        _identity = identity;
        _trust = trust;
        _peers = peers;
        _backend = backend ?? new AvahiDBus();
    }

    public int Port => _port;

    public async Task StartAsync()
    {
        // Dual-stack: bound to IPv6Any with IPv6Only off, so an IPv4-mapped
        // connection from a peer that only resolved an A record still lands
        // here. PROTOCOL.md §3 is IPv6-only by design, but refusing IPv4
        // outright would turn a degraded network into no sync at all.
        _listener = new TcpListener(IPAddress.IPv6Any, 0);
        _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();

        if (!await _backend.IsAvailableAsync())
            throw new InvalidOperationException(
                $"discovery backend {_backend.Name} is unavailable — is avahi-daemon running?");

        await PublishAsync();
        await _backend.BrowseAsync(OnDiscovered);
        _ = MaintenanceLoop();

        Identity.Log($"Discovery: listening on port {_port}, backend={_backend.Name}");
    }

    // ---- advertise --------------------------------------------------

    private async Task PublishAsync()
    {
        _publishedPending = _trust.IsEmpty;
        await _backend.PublishAsync(Environment.MachineName, _port, new Dictionary<string, string>
        {
            ["v"] = "1",
            ["did"] = _identity.DidHex,
            ["name"] = Environment.MachineName,
            // Advertising `stream` matches the Mac. Windows omits it here,
            // harmlessly: capability gating is on the Hello's caps, never
            // the TXT record. See HANDOFF.md.
            ["caps"] = "text,image,files,rich,stream",
            ["pend"] = _trust.IsEmpty ? "1" : "0",
        });
    }

    /// Re-advertise if the `pend` flag no longer matches reality — i.e. the
    /// first peer has just been trusted, so this device is no longer looking
    /// to be paired. Cheap and idempotent; a no-op when nothing changed.
    public async Task RefreshAdvertisementAsync()
    {
        if (_publishedPending == _trust.IsEmpty) return;
        try { await PublishAsync(); }
        catch (Exception ex) { Identity.Log($"Discovery: republish failed: {ex.Message}"); }
    }

    // ---- browse -----------------------------------------------------

    private void OnDiscovered(DiscoveredService svc)
    {
        if (svc.DidHex == _identity.DidHex) return;   // ourselves

        var key = svc.DidHex;
        var endpoint = new IPEndPoint(svc.Address, svc.Port);

        // One peer resolves once per interface and per address family, so
        // this is called repeatedly for the same device. Accumulate the
        // addresses rather than replacing: the first one to answer wins at
        // connect time, and which that is varies by network.
        var entry = _endpoints.GetOrAdd(key, _ => new Endpoint(new List<IPEndPoint>()));
        lock (entry.Addresses)
        {
            if (!entry.Addresses.Contains(endpoint)) entry.Addresses.Add(endpoint);
        }

        var trusted = _trust.Contains(key);
        _peers.OnDiscovered(key, svc.Name, trusted);

        if (trusted) TryConnect(key);
    }

    // ---- connect ----------------------------------------------------

    /// Called after the user clicks Trust: dial straight away using the
    /// cached endpoint rather than waiting for the next announcement.
    public void ConnectToPeer(string didHex)
    {
        var key = didHex.ToLowerInvariant();
        TryConnect(key);
        _ = RefreshAdvertisementAsync();
    }

    private void TryConnect(string key)
    {
        if (_peers.IsConnected(key)) return;
        if (!_endpoints.TryGetValue(key, out var endpoint))
        {
            Identity.Log($"Discovery.TryConnect: no cached endpoint for {key[..8]}");
            return;
        }
        if (!_connecting.TryAdd(key, 0)) return;      // an attempt is already in flight
        _ = ConnectAsync(key, endpoint);
    }

    private async Task ConnectAsync(string key, Endpoint endpoint)
    {
        try
        {
            List<IPEndPoint> targets;
            lock (endpoint.Addresses) targets = endpoint.Addresses.ToList();

            // IPv6 first: that is what the protocol is specified on, and a
            // peer advertising both will usually be reachable on either.
            foreach (var target in targets.OrderByDescending(
                         t => t.AddressFamily == AddressFamily.InterNetworkV6))
            {
                if (_peers.IsConnected(key)) return;

                TcpClient? client = null;
                try
                {
                    using var cts = new CancellationTokenSource(ConnectTimeout);
                    client = new TcpClient(target.AddressFamily);
                    await client.ConnectAsync(target.Address, target.Port, cts.Token);

                    var pc = new PeerConnection(client, _identity, _trust, PeerRole.Client);
                    _peers.Adopt(pc);
                    pc.Start();
                    Identity.Log($"Discovery: connected to {key[..8]} at {target}");
                    return;
                }
                catch (Exception ex)
                {
                    client?.Dispose();
                    Identity.Log($"Discovery: connect {target} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Every advertised address refused. That is evidence, not a
            // guess, so the registry can stop saying "Looking…".
            _peers.MarkUnreachable(key);
        }
        finally
        {
            _connecting.TryRemove(key, out _);
        }
    }

    private async Task AcceptLoop()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { break; }

            _ = Task.Run(() =>
            {
                var pc = new PeerConnection(client, _identity, _trust, PeerRole.Server);
                _peers.Adopt(pc);
                pc.Start();
            });
        }
    }

    /// Retry trusted peers that are not currently connected. Covers the
    /// window after a peer reboots, and the case where our connect lost the
    /// race with theirs.
    private async Task MaintenanceLoop()
    {
        while (true)
        {
            await Task.Delay(MaintenanceInterval);
            try
            {
                foreach (var entry in _trust.All())
                {
                    var key = entry.DidHex.ToLowerInvariant();
                    if (!_peers.IsConnected(key)) TryConnect(key);
                }
                await RefreshAdvertisementAsync();
            }
            catch (Exception ex)
            {
                Identity.Log($"Discovery: maintenance error: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        await _backend.DisposeAsync();
    }
}
