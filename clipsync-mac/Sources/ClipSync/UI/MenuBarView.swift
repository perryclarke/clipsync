import SwiftUI

/// The status-item menu (`menuBarExtraStyle(.menu)`): a real menu, not a
/// custom popover window. Disabled text items report state; buttons are
/// labelled with the verb — they say what pressing them will do.
struct MenuBarView: View {
    @EnvironmentObject var coordinator: AppCoordinator

    var body: some View {
        // State lines: what is currently true.
        Text(coordinator.globalPaused ? "ClipSync — Paused" : "ClipSync")
        Text("This Mac: \(myFingerprint)")

        Divider()

        if coordinator.peerList.isEmpty {
            Text("Looking for peers on the local network…")
        } else {
            ForEach(coordinator.peerList) { peer in
                peerMenu(peer)
            }
        }

        Divider()

        Button("Settings…") {
            SettingsWindowController.shared.show(coordinator: coordinator)
        }
        Button(coordinator.globalPaused ? "Resume Syncing" : "Pause Syncing") {
            coordinator.setGlobalPause(!coordinator.globalPaused)
        }

        Divider()

        Button("Quit ClipSync") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    @ViewBuilder
    private func peerMenu(_ peer: Peer) -> some View {
        let paused = coordinator.isPeerPaused(peer.didHex)
        Menu("\(peer.name) — \(stateLabel(peer, paused: paused))") {
            // Shown so the fingerprint can be eyeball-checked against the
            // other machine when pairing.
            Text("Fingerprint: \(peer.fingerprintShort)")
            if peer.state == .pending {
                Button("Trust \(peer.name)") {
                    coordinator.trust(didHex: peer.didHex)
                }
            } else {
                Button(paused ? "Resume Syncing to \(peer.name)"
                              : "Pause Syncing to \(peer.name)") {
                    coordinator.setPeerPaused(peer.didHex, paused: !paused)
                }
            }
        }
    }

    private func stateLabel(_ peer: Peer, paused: Bool) -> String {
        // A muted online peer reads "Paused", because the reason nothing
        // reaches it is the mute, not the network.
        if paused && peer.state == .online { return "Paused" }
        switch peer.state {
        case .online: return "Online"
        case .pending: return "Not trusted"
        case .offline: return "Offline"
        }
    }

    private var myFingerprint: String {
        String(Identity.shared.didHex.prefix(8))
    }
}
