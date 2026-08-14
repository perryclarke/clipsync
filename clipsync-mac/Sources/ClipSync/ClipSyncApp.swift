import SwiftUI
import AppKit

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
            ZStack {
                Image(systemName: "list.clipboard.fill")
                Image(systemName: "wifi")
                    .font(.system(size: 10, weight: .bold))
                    .offset(y: -1)
                    .foregroundStyle(.blue)
            }
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

    @Published var peerList: [Peer] = []
    @Published var recentItems: [RecentItem] = []

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
        self.peers = PeerRegistry()
        self.writer = PasteboardWriter()
        self.watcher = PasteboardWatcher(identity: identity, writer: writer)
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

        watcher.start()
        discovery.start()
    }

    func trust(didHex: String) { peers.trust(didHex: didHex) }
}

struct RecentItem: Identifiable {
    let id = UUID()
    let item: ClipboardItem
    let direction: Direction
    enum Direction { case incoming, outgoing }
}
