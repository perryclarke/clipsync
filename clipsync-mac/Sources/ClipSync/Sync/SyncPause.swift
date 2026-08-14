import Foundation

/// Whether this device is currently sending what it copies, and to whom.
///
/// Two independent gates, both of which must be open for an item to go
/// out: a global pause covering every peer, and a per-peer mute. They are
/// not one shared switch — resuming a single peer must not quietly defeat
/// a global pause, and resuming globally must not un-mute a peer the user
/// muted on purpose.
///
/// Only sending is affected. Items from peers still arrive and still land
/// in the local clipboard while paused; nothing is queued and nothing is
/// replayed on resume.
///
/// The global pause is deliberately not persisted. It means "not right
/// now", and an app that came back from a restart still silently not
/// syncing would be a bad surprise. Per-peer mutes are a lasting
/// preference about that machine, so those persist — see AppSettings.
final class SyncPause {
    private let settings: AppSettings
    private let lock = NSLock()
    private var _globalPaused = false

    /// Fired whenever anything here changes, so the UI can redraw. Fired
    /// outside the lock; handlers must not assume a thread.
    var onChange: (() -> Void)?

    init(settings: AppSettings) {
        self.settings = settings
    }

    var globalPaused: Bool {
        get { lock.lock(); defer { lock.unlock() }; return _globalPaused }
        set {
            lock.lock()
            let changed = _globalPaused != newValue
            _globalPaused = newValue
            lock.unlock()
            guard changed else { return }
            NSLog("SyncPause: global pause %@", newValue ? "on" : "off")
            onChange?()
        }
    }

    var mutedPeers: [String] { settings.pausedPeers }

    func isMuted(_ didHex: String) -> Bool { settings.isPeerPaused(didHex) }

    func setMuted(_ didHex: String, muted: Bool) {
        guard isMuted(didHex) != muted else { return }
        settings.setPeerPaused(didHex, paused: muted)
        // Re-read rather than trusting the call: a blank DID is ignored by
        // the store, and firing onChange for a no-op would be a lie.
        guard isMuted(didHex) == muted else { return }
        NSLog("SyncPause: peer %@ %@", String(didHex.prefix(8)), muted ? "muted" : "unmuted")
        onChange?()
    }

    /// The one question the send path asks.
    func shouldSend(to didHex: String) -> Bool {
        !globalPaused && !isMuted(didHex)
    }
}
