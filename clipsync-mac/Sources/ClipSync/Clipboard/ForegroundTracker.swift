import AppKit
import Foundation

/// Feeds the ForegroundRing from NSWorkspace's app-activation
/// notifications. Event-driven, no polling; the ring answers the
/// suppression policy's queries about which app held focus and when.
final class ForegroundTracker {
    let ring = ForegroundRing()

    private var observer: NSObjectProtocol?
    /// ClipSync's own bundle id, kept out of the ring so focusing our own
    /// settings window never attributes a copy to us and the app can never
    /// end up excluding itself (the macOS analogue of Windows'
    /// WINEVENT_SKIPOWNPROCESS).
    private let ownBundleId = Bundle.main.bundleIdentifier?.lowercased()

    func start() {
        // Seed from the current frontmost app so the first copy after
        // launch resolves rather than reading back as unknown.
        record(NSWorkspace.shared.frontmostApplication, at: Date())

        observer = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification,
            object: nil,
            queue: .main
        ) { [weak self] note in
            let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            self?.record(app, at: Date())
        }
    }

    func stop() {
        if let observer {
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
        }
        observer = nil
    }

    private func record(_ app: NSRunningApplication?, at: Date) {
        let identity = ForegroundTracker.resolve(app)
        if let identity, identity.key == ownBundleId { return }
        ring.record(at: at, app: identity)
    }

    /// NSRunningApplication → AppIdentity, or nil when it cannot be
    /// resolved (which the decision treats as fail-open). This is the
    /// resolver whose key must equal the key the picker stores (§2.2);
    /// both go through AppIdentity's one normalisation.
    static func resolve(_ app: NSRunningApplication?) -> AppIdentity? {
        guard let app, let bundleId = app.bundleIdentifier else { return nil }
        return AppIdentity(
            kind: .bundle,
            key: bundleId,
            displayName: app.localizedName ?? bundleId,
            path: app.bundleURL?.path
        )
    }
}
