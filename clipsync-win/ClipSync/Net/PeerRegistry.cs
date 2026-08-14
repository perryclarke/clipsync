using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ClipSync.Net;

public sealed record Peer(string DidHex, string Name, PeerState State);
public enum PeerState { Online, Pending, Offline }

public sealed class PeerRegistry
{
    public Action<ClipboardItem>? OnRemoteItem;
    public Action<IReadOnlyList<Peer>>? OnChange;

    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Peer> _discovered = new();
    private readonly string _localDidHex;

    public PeerRegistry(string localDidHex) { _localDidHex = localDidHex; }

    public bool IsConnected(string didHex) => _connections.ContainsKey(didHex.ToLowerInvariant());

    /// Called by Discovery whenever a service instance is seen on the network.
    public void OnDiscovered(string didHex, string name, bool trusted)
    {
        var key = didHex.ToLowerInvariant();
        var state = trusted ? PeerState.Offline : PeerState.Pending;
        _discovered[key] = new Peer(key, name, state);
        Security.Identity.Log($"PeerRegistry.OnDiscovered: {name} did={key} trusted={trusted} state={state}");
        Emit();
    }

    public void Adopt(PeerConnection pc)
    {
        pc.OnItem = item =>
        {
            try { OnRemoteItem?.Invoke(item); }
            catch (Exception ex) { Security.Identity.Log($"OnRemoteItem error: {ex.Message}"); }
        };
        pc.OnReady = () =>
        {
            if (pc.PeerDid is not { } did) return;
            var hex = Convert.ToHexString(did).ToLowerInvariant();
            while (true)
            {
                if (_connections.TryAdd(hex, pc)) break;
                if (!_connections.TryGetValue(hex, out var existing)) continue;
                if (ReferenceEquals(existing, pc)) break;
                // Simultaneous connect from both sides. Both ends must keep
                // the *same* connection, so tie-break deterministically:
                // keep the one where the lower-DID device is the TLS client.
                var keepClient = string.CompareOrdinal(_localDidHex, hex) < 0;
                if ((pc.Role == PeerRole.Client) == keepClient)
                {
                    if (_connections.TryUpdate(hex, pc, existing))
                    {
                        Security.Identity.Log($"PeerRegistry: replacing duplicate connection for {hex}");
                        existing.Close();
                        break;
                    }
                }
                else
                {
                    Security.Identity.Log($"PeerRegistry: dropping duplicate connection for {hex}");
                    pc.Close();
                    return;
                }
            }
            Emit();
        };
        pc.OnClose = () =>
        {
            if (pc.PeerDid is { } did)
            {
                var hex = Convert.ToHexString(did).ToLowerInvariant();
                // Only remove if this pc is still the registered connection —
                // a replaced duplicate must not evict its successor.
                _connections.TryRemove(new KeyValuePair<string, PeerConnection>(hex, pc));
            }
            Emit();
        };
    }

    public IReadOnlyList<Peer> GetAll()
    {
        var list = new List<Peer>();
        var connected = new HashSet<string>();
        Security.Identity.Log($"GetAll: _connections.Count={_connections.Count}");
        foreach (var (hex, pc) in _connections)
        {
            Security.Identity.Log($"  connected: {hex} name={pc.PeerName}");
            list.Add(new Peer(hex, pc.PeerName ?? "Peer", PeerState.Online));
            connected.Add(hex);
        }
        foreach (var (hex, peer) in _discovered)
        {
            if (!connected.Contains(hex))
                list.Add(peer);
        }
        Security.Identity.Log($"GetAll: returning {list.Count} peers");
        return list;
    }

    /// Consulted for each peer before sending. Null means send to everyone, which
    /// is what this class did before pausing existed and what it still does
    /// if nobody wires it up. Keeping the decision outside means the registry
    /// does not need to know what a pause is, and the global and per-peer
    /// cases both arrive through one predicate.
    public Func<string, bool>? ShouldSendTo;

    public void Broadcast(ClipboardItem item)
    {
        foreach (var (hex, pc) in _connections)
        {
            // Both branches log. A skip needs saying, or a user watches
            // nothing arrive with no way to tell a pause from a broken link;
            // and a send needs saying too, or the absence of a skip line is
            // indistinguishable from the item never reaching this method.
            if (ShouldSendTo is { } gate && !gate(hex))
            {
                Security.Identity.Log($"Broadcast: not sending to {hex[..8]} (paused)");
                continue;
            }
            Security.Identity.Log($"Broadcast: sending to {hex[..8]}");
            _ = pc.SendItemAsync(item);
        }
    }

    private void Emit()
    {
        var list = new List<Peer>();
        var connected = new HashSet<string>();
        foreach (var (hex, pc) in _connections)
        {
            list.Add(new Peer(hex, pc.PeerName ?? "Peer", PeerState.Online));
            connected.Add(hex);
        }
        foreach (var (hex, peer) in _discovered)
        {
            if (!connected.Contains(hex))
                list.Add(peer);
        }
        Security.Identity.Log($"PeerRegistry.Emit: {list.Count} peers, OnChange={OnChange is not null}");
        OnChange?.Invoke(list);
    }
}
