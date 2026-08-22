import SwiftUI
import AppKit
import ServiceManagement

@main
struct ClipSyncApp: App {
    @StateObject private var coordinator = AppCoordinator()

    init() {
        NSApplication.shared.setActivationPolicy(.accessory)
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarView()
                .environmentObject(coordinator)
        } label: {
            // The status item is where a paused device says so without
            // the user having to go looking — the macOS analogue of the
            // Windows tray-icon badge. Composed as an NSImage because
            // MenuBarExtra template-renders label views, stripping colour.
            Image(nsImage: StatusIcon.make(paused: coordinator.globalPaused))
                .accessibilityLabel(coordinator.globalPaused ? "ClipSync, paused" : "ClipSync")
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
final class AppCoordinator: ObservableObject {
    let identity: Identity
    let trustStore: TrustStore
    let watcher: PasteboardWatcher
    let writer: PasteboardWriter
    let discovery: Discovery
    let peers: PeerRegistry
    let settings: AppSettings
    let foreground: ForegroundTracker
    let syncPause: SyncPause

    @Published var peerList: [Peer] = []
    @Published var recentItems: [RecentItem] = []
    /// Mirrors of SyncPause / AppSettings state, republished so SwiftUI
    /// redraws. The senders of truth stay in those objects.
    @Published var globalPaused = false
    @Published var pausedPeers: Set<String> = []
    @Published var excludedApps: [AppIdentity] = []
    @Published var hiddenPeers: [HiddenPeer] = []

    /// The peer list with hidden devices filtered out — what the popover
    /// shows. Hiding is a display preference only (see AppSettings.hide).
    var visiblePeers: [Peer] {
        let hidden = Set(hiddenPeers.map(\.didHex))
        return peerList.filter { !hidden.contains($0.didHex.lowercased()) }
    }

    init() {
        // `--reset` forgets all trusted peers so the user must re-approve
        // connections. Run this first — before Identity.loadOrCreate(), which
        // can block on a keychain prompt — so the reset fires immediately at
        // launch and before the store is loaded into memory below. This
        // mirrors Windows, where reset runs before Application.Start.
        if CommandLine.arguments.contains("--reset") {
            TrustStore.reset()
        }
        self.identity = Identity.loadOrCreate()
        self.trustStore = TrustStore.load()
        self.settings = AppSettings.load()
        self.foreground = ForegroundTracker()
        self.syncPause = SyncPause(settings: settings)
        self.peers = PeerRegistry()
        self.writer = PasteboardWriter()
        self.watcher = PasteboardWatcher(identity: identity, writer: writer,
                                         foreground: foreground.ring, settings: settings)
        self.discovery = Discovery(identity: identity, trustStore: trustStore, peers: peers)

        watcher.onLocalCopy = { [weak self] item in
            self?.peers.broadcast(item)
            Task { @MainActor in self?.recentItems.insert(RecentItem(item: item, direction: .outgoing), at: 0) }
        }
        peers.onRemoteItem = { [weak self] item in
            self?.writer.apply(item)
            Task { @MainActor in self?.recentItems.insert(RecentItem(item: item, direction: .incoming), at: 0) }
        }
        peers.onChange = { [weak self] list in
            Task { @MainActor in self?.peerList = list }
        }

        // The send gate: one predicate covering the global pause and the
        // per-peer mutes. The registry never learns what a pause is.
        peers.shouldSendTo = { [syncPause] didHex in syncPause.shouldSend(to: didHex) }
        syncPause.onChange = { [weak self] in
            Task { @MainActor in self?.refreshPauseState() }
        }

        refreshPauseState()
        excludedApps = settings.excluded
        hiddenPeers = settings.hidden

        foreground.start()
        watcher.start()
        discovery.start()
    }

    func trust(didHex: String) { peers.trust(didHex: didHex) }

    // MARK: Pause / resume

    func setGlobalPause(_ paused: Bool) { syncPause.globalPaused = paused }

    func setPeerPaused(_ didHex: String, paused: Bool) {
        syncPause.setMuted(didHex, muted: paused)
    }

    func isPeerPaused(_ didHex: String) -> Bool { pausedPeers.contains(didHex.lowercased()) }

    private func refreshPauseState() {
        globalPaused = syncPause.globalPaused
        pausedPeers = Set(syncPause.mutedPeers)
    }

    // MARK: Excluded apps

    func addExclusion(_ app: AppIdentity) {
        settings.add(app)
        excludedApps = settings.excluded
    }

    func removeExclusion(_ app: AppIdentity) {
        settings.remove(app)
        excludedApps = settings.excluded
    }

    // MARK: Hidden devices

    func hidePeer(_ didHex: String, name: String) {
        settings.hide(didHex, name: name)
        hiddenPeers = settings.hidden
    }

    func unhidePeer(_ didHex: String) {
        settings.unhide(didHex)
        hiddenPeers = settings.hidden
    }

    // MARK: Open at login

    /// SMAppService needs a real app bundle; under a bare `swift run`
    /// registration fails, so the settings toggle disables itself.
    var canOpenAtLogin: Bool { Bundle.main.bundleIdentifier != nil }

    var opensAtLogin: Bool { SMAppService.mainApp.status == .enabled }

    func setOpensAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
        } catch {
            NSLog("open-at-login %@ failed: %@", on ? "register" : "unregister",
                  String(describing: error))
        }
    }

    // MARK: Start over

    /// Forget every trusted device and preference, then relaunch as
    /// first-run. Identity is deliberately kept, and peers are not told;
    /// both sides re-trust to reconnect. Relaunching rather than
    /// un-picking live state: discovery, the registry and every open
    /// connection were built on the trust just erased, and a fresh
    /// process is the one way to be sure nothing remembers it.
    func startOver() {
        NSLog("start over: clearing trust and settings, relaunching")
        trustStore.clear()
        settings.resetAll()

        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL,
                                           configuration: config) { _, _ in }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            NSApp.terminate(nil)
        }
    }
}

struct RecentItem: Identifiable {
    let id = UUID()
    let item: ClipboardItem
    let direction: Direction
    enum Direction { case incoming, outgoing }
}
