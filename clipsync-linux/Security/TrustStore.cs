using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClipSync.Platform;
using ClipSync.Platform.Backends;

namespace ClipSync.Security;

/// Peers whose certificate we have pinned, keyed by lowercase did hex.
///
/// Same public surface and same JSON shape as clipsync-win's TrustStore, so
/// the linked PeerConnection compiles against it unchanged and a trust file
/// means the same thing on either platform. Only the at-rest protection
/// differs: DPAPI there, an owner-only file here.
///
/// A corrupt file degrades to empty rather than throwing — matching the
/// Windows behaviour. That is deliberate: an unreadable trust store makes
/// peers untrusted, which is a visible "click Trust again", whereas
/// throwing at startup would make the app simply not run.
public sealed class TrustStore
{
    public sealed record Entry(string DidHex, string Name, DateTime AddedAt);

    private const string StoreKey = "trust.json";

    private readonly Dictionary<string, Entry> _entries;
    private readonly ISecretStore _store;
    private readonly object _lock = new();

    private TrustStore(ISecretStore store, Dictionary<string, Entry> entries)
    {
        _store = store;
        _entries = entries;
    }

    public static TrustStore Load() => Load(Identity.DefaultStore());

    public static TrustStore Load(ISecretStore store)
    {
        try
        {
            if (store.Load(StoreKey) is { } raw)
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(raw) ?? new();
                return new TrustStore(store, map);
            }
        }
        catch { /* fall through to empty */ }
        return new TrustStore(store, new Dictionary<string, Entry>());
    }

    public bool IsEmpty { get { lock (_lock) return _entries.Count == 0; } }

    public bool Contains(string didHex)
    {
        lock (_lock) return _entries.ContainsKey(didHex.ToLowerInvariant());
    }

    public bool Contains(byte[] spkiHash)
        => Contains(Convert.ToHexString(spkiHash).ToLowerInvariant());

    public void Add(string didHex, string name)
    {
        lock (_lock)
        {
            _entries[didHex.ToLowerInvariant()] = new Entry(didHex, name, DateTime.UtcNow);
            Persist();
        }
    }

    public void Remove(string didHex)
    {
        lock (_lock)
        {
            _entries.Remove(didHex.ToLowerInvariant());
            Persist();
        }
    }

    /// Forget every trusted peer. After this the device advertises pend=1
    /// again and rejects previously-trusted peers until they re-pair.
    /// Used by `--reset` and by "Start over".
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Persist();
        }
    }

    public IReadOnlyList<Entry> All()
    {
        lock (_lock) return _entries.Values.ToList();
    }

    private void Persist() => _store.Save(StoreKey, JsonSerializer.SerializeToUtf8Bytes(_entries));
}
