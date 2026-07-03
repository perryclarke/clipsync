using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClipSync.Security;

public sealed class TrustStore
{
    public sealed record Entry(string DidHex, string Name, DateTime AddedAt);

    private readonly Dictionary<string, Entry> _entries;
    private readonly string _path;
    private readonly object _lock = new();

    private TrustStore(string path, Dictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    public static TrustStore Load()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSync");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "trust.json.dpapi");
        if (File.Exists(path))
        {
            try
            {
                var raw = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(raw) ?? new();
                return new TrustStore(path, map);
            }
            catch { /* fall through */ }
        }
        return new TrustStore(path, new Dictionary<string, Entry>());
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
    /// Used by the `--reset` command-line switch.
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

    private void Persist()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(_entries);
        File.WriteAllBytes(_path, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }
}
