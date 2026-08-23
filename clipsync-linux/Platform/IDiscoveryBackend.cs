using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace ClipSync.Platform;

/// One resolved advertisement of a peer. A single peer produces several of
/// these — mDNS resolves per network interface and per IP protocol — so
/// consumers must treat them as additive rather than as distinct peers.
public sealed record DiscoveredService(
    string DidHex,
    string Name,
    IPAddress Address,
    int Port,
    IReadOnlyDictionary<string, string> Txt);

/// How this machine announces itself and finds peers.
///
/// A swappable per-flavour seam: Avahi is near-universal on desktop Linux,
/// but a systemd-resolved-only or container host may have no responder at
/// all, and running our own would collide with whatever owns port 5353.
/// Choosing at startup keeps that decision out of the protocol layer.
public interface IDiscoveryBackend : IAsyncDisposable
{
    /// Short stable name, used in logs and the CLIPSYNC_BACKEND_DISCOVERY
    /// override.
    string Name { get; }

    /// Whether this backend can be used here. Should not throw.
    Task<bool> IsAvailableAsync();

    /// Announce a service instance. Calling again replaces the previous
    /// advertisement — used when the TXT record changes, notably `pend`
    /// when the trust store stops being empty.
    Task PublishAsync(string serviceName, int port, IReadOnlyDictionary<string, string> txt);

    /// Start browsing. `onFound` is called for every resolved advertisement,
    /// possibly many times for the same peer.
    Task BrowseAsync(Action<DiscoveredService> onFound);
}
