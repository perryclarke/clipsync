using System;

namespace ClipSync.Settings;

public enum AppKind
{
    /// Desktop (Win32) application, keyed by executable file name.
    Exe,
    /// Packaged (Store/UWP) application, keyed by package family name.
    Package,
}

/// Names an application for exclusion purposes.
///
/// Equality is deliberately on Kind+Key only: DisplayName and Path are
/// presentation data, so an app that is renamed or moves to a new
/// versioned install directory still matches a saved exclusion.
public sealed record AppIdentity
{
    public AppKind Kind { get; }

    /// Normalised match key. Lowercased throughout; for Exe this is the
    /// bare file name, so `app-1.0.9\Discord.exe` and
    /// `app-1.0.10\Discord.exe` are the same app.
    public string Key { get; }

    public string DisplayName { get; }

    /// Full path the user originally picked, kept only so the UI can show
    /// which `javaw.exe` (or similar ambiguous name) this refers to.
    public string? Path { get; }

    public AppIdentity(AppKind kind, string key, string displayName, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
        Kind = kind;
        Key = Normalise(kind, key);
        DisplayName = displayName;
        Path = path;
    }

    private static string Normalise(AppKind kind, string key) => kind switch
    {
        AppKind.Exe => System.IO.Path.GetFileName(key.Trim()).ToLowerInvariant(),
        _ => key.Trim().ToLowerInvariant(),
    };

    public bool Equals(AppIdentity? other) =>
        other is not null && Kind == other.Kind && Key == other.Key;

    public override int GetHashCode() => HashCode.Combine(Kind, Key);
}
