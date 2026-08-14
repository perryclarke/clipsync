using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipSync.Settings;

/// User preferences, stored as plain JSON in %LOCALAPPDATA%\ClipSync.
/// Unlike the trust store this is not secret, and being hand-editable is
/// a feature. A corrupt file degrades to defaults rather than throwing,
/// matching TrustStore.Load().
public sealed class AppSettings
{
    private const int CurrentVersion = 1;

    private readonly string _path;
    private readonly List<AppIdentity> _excluded;
    private readonly List<string> _pausedPeers;
    private readonly object _lock = new();

    private AppSettings(string path, List<AppIdentity> excluded, List<string> pausedPeers)
    {
        _path = path;
        _excluded = excluded;
        _pausedPeers = pausedPeers;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipSync", "settings.json");

    public static AppSettings Load() => Load(DefaultPath);

    public static AppSettings Load(string path)
    {
        var list = new List<AppIdentity>();
        var paused = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions);
                foreach (var e in model?.ExcludedApps ?? new List<FileModel.Entry>())
                {
                    if (TryParse(e, out var id)) list.Add(id);
                }
                foreach (var did in model?.PausedPeers ?? new List<string>())
                {
                    if (Normalise(did) is { } key && !paused.Contains(key)) paused.Add(key);
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable: start empty rather than crashing the app.
            Security.Log.Write($"AppSettings: could not read {path}: {ex.GetType().Name}; using defaults");
            list.Clear();
            paused.Clear();
        }
        return new AppSettings(path, list, paused);
    }

    public IReadOnlyList<AppIdentity> Excluded
    {
        get { lock (_lock) return _excluded.ToList(); }
    }

    public bool IsExcluded(AppIdentity app)
    {
        lock (_lock) return _excluded.Contains(app);
    }

    public void Add(AppIdentity app)
    {
        lock (_lock)
        {
            if (_excluded.Contains(app)) return;
            _excluded.Add(app);
            Persist();
        }
    }

    public void Remove(AppIdentity app)
    {
        lock (_lock)
        {
            if (_excluded.RemoveAll(e => e.Equals(app)) == 0) return;
            Persist();
        }
    }

    /// Peers this device does not send to, by lowercase DID hex.
    ///
    /// Only the per-peer pause lives here. A global pause is deliberately not
    /// persisted: it means "not right now", and a restart that silently left
    /// syncing off would be a bad surprise.
    public IReadOnlyList<string> PausedPeers
    {
        get { lock (_lock) return _pausedPeers.ToList(); }
    }

    public bool IsPeerPaused(string didHex)
    {
        if (Normalise(didHex) is not { } key) return false;
        lock (_lock) return _pausedPeers.Contains(key);
    }

    public void SetPeerPaused(string didHex, bool paused)
    {
        if (Normalise(didHex) is not { } key) return;
        lock (_lock)
        {
            var changed = paused ? Add(key) : _pausedPeers.Remove(key);
            if (changed) Persist();
        }

        bool Add(string k)
        {
            if (_pausedPeers.Contains(k)) return false;
            _pausedPeers.Add(k);
            return true;
        }
    }

    /// DIDs are compared lowercase; blank ones are not a peer.
    private static string? Normalise(string? didHex) =>
        string.IsNullOrWhiteSpace(didHex) ? null : didHex.Trim().ToLowerInvariant();

    /// Caller holds _lock.
    private void Persist()
    {
        try
        {
            var model = new FileModel
            {
                Version = CurrentVersion,
                ExcludedApps = _excluded.Select(e => new FileModel.Entry
                {
                    Kind = e.Kind == AppKind.Exe ? "exe" : "package",
                    Key = e.Key,
                    Name = e.DisplayName,
                    Path = e.Path,
                }).ToList(),
                PausedPeers = _pausedPeers.ToList(),
            };

            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write-then-move so a crash mid-write cannot leave a corrupt file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // In-memory list keeps working for this session.
            Security.Log.Write($"AppSettings: could not write {_path}: {ex.GetType().Name}");
        }
    }

    private static bool TryParse(FileModel.Entry e, out AppIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(e.Key)) return false;

        AppKind kind;
        switch (e.Kind?.ToLowerInvariant())
        {
            case "exe": kind = AppKind.Exe; break;
            case "package": kind = AppKind.Package; break;
            default: return false;   // e.g. macOS "bundle" entries
        }

        identity = new AppIdentity(kind, e.Key!, e.Name ?? e.Key!, e.Path);
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class FileModel
    {
        public int Version { get; set; } = CurrentVersion;
        public List<Entry> ExcludedApps { get; set; } = new();
        public List<string> PausedPeers { get; set; } = new();

        public sealed class Entry
        {
            public string? Kind { get; set; }
            public string? Key { get; set; }
            public string? Name { get; set; }
            public string? Path { get; set; }
        }
    }
}
