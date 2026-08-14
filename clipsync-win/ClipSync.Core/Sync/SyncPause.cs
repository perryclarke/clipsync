using System;
using System.Collections.Generic;
using ClipSync.Settings;

namespace ClipSync.Sync;

/// Whether this device is currently sending what it copies, and to whom.
///
/// Two independent gates, both of which must be open for an item to go out:
/// a global pause covering every peer, and a per-peer mute. They are not one
/// shared switch -- resuming a single peer must not quietly defeat a global
/// pause, and resuming globally must not un-mute a peer the user muted on
/// purpose.
///
/// Only sending is affected. Items from peers still arrive and still land in
/// the local clipboard while paused; nothing is queued and nothing is
/// replayed on resume.
///
/// The global pause is deliberately not persisted. It means "not right now",
/// and an app that came back from a restart still silently not syncing would
/// be a bad surprise. Per-peer mutes are a lasting preference about that
/// machine, so those do persist -- see AppSettings.
public sealed class SyncPause
{
    private readonly AppSettings _settings;
    private volatile bool _globalPaused;

    public SyncPause(AppSettings settings) => _settings = settings;

    /// Raised whenever anything here changes, so the UI can redraw. Fired
    /// outside any lock; handlers must not assume a thread.
    public event Action? Changed;

    public bool GlobalPaused
    {
        get => _globalPaused;
        set
        {
            if (_globalPaused == value) return;
            _globalPaused = value;
            Security.Log.Write($"SyncPause: global pause {(value ? "on" : "off")}");
            Changed?.Invoke();
        }
    }

    public IReadOnlyList<string> MutedPeers => _settings.PausedPeers;

    public bool IsMuted(string didHex) => _settings.IsPeerPaused(didHex);

    public void SetMuted(string didHex, bool muted)
    {
        if (IsMuted(didHex) == muted) return;
        _settings.SetPeerPaused(didHex, muted);
        // Re-read rather than trusting the call: a blank DID is ignored by
        // the store, and firing Changed for a no-op would be a lie.
        if (IsMuted(didHex) != muted) return;
        Security.Log.Write($"SyncPause: peer {Short(didHex)} {(muted ? "muted" : "unmuted")}");
        Changed?.Invoke();
    }

    /// The one question the send path asks.
    public bool ShouldSendTo(string didHex) => !_globalPaused && !IsMuted(didHex);

    private static string Short(string didHex) =>
        string.IsNullOrEmpty(didHex) ? "(none)" : didHex[..Math.Min(8, didHex.Length)];
}
