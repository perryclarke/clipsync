using System;

namespace ClipSync.Platform;

/// Where private key material lives at rest.
///
/// One of the swappable per-flavour seams: Windows has DPAPI and macOS has
/// the Keychain, but Linux has no single answer — a GNOME session has
/// libsecret, a KDE one has KWallet, and a headless or newly-installed box
/// may have neither unlocked. So the store is chosen at startup rather than
/// assumed, and the only implementation today is the one that always works.
///
/// Implementations must be safe to call before any desktop session exists:
/// the daemon can start at login, ahead of the keyring being unlocked.
public interface ISecretStore
{
    /// Short stable name, used in logs and in the CLIPSYNC_BACKEND_SECRETS
    /// override.
    string Name { get; }

    /// Whether this store can be used in the current environment.
    bool IsAvailable();

    /// Read a previously stored secret, or null if absent.
    byte[]? Load(string key);

    /// Store a secret, replacing any previous value under the same key.
    void Save(string key, byte[] value);

    /// Remove a secret. Absent keys are not an error.
    void Delete(string key);
}
