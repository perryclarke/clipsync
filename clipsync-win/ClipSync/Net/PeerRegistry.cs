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
    private readonly ConcurrentDictionary<string, Peer> _pending = new();

    public bool IsConnected(string didHex) => _connections.ContainsKey(didHex);

    public void Adopt(PeerConnection pc)
    {
        pc.OnItem = item => OnRemoteItem?.Invoke(item);
        pc.OnReady = () =>
        {
            if (pc.PeerDid is { } did)
            {
                var hex = Convert.ToHexString(did).ToLowerInvariant();
                _connections[hex] = pc;
                Emit();
            }
        };
        pc.OnClose = () =>
        {
            if (pc.PeerDid is { } did)
            {
                _connections.TryRemove(Convert.ToHexString(did).ToLowerInvariant(), out _);
                Emit();
            }
        };
    }

    public void Broadcast(ClipboardItem item)
    {
        foreach (var pc in _connections.Values) _ = pc.SendItemAsync(item);
    }

    private void Emit()
    {
        var list = new List<Peer>();
        foreach (var (hex, pc) in _connections)
            list.Add(new Peer(hex, pc.PeerName ?? "Peer", PeerState.Online));
        list.AddRange(_pending.Values.Where(p => !_connections.ContainsKey(p.DidHex)));
        OnChange?.Invoke(list);
    }
}
