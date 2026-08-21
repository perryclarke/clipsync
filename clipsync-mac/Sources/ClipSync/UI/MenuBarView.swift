import SwiftUI

/// The status-item popover (`menuBarExtraStyle(.window)`): a custom card,
/// not the native menu, so it can carry the same affordances as the
/// Windows tray popup — a header with this Mac's identity, peer rows with a
/// coloured status dot and an inline pause/trust control, and icon action
/// rows. Unlike a menu it stays open across clicks, so pausing a peer
/// doesn't dismiss it.
struct MenuBarView: View {
    @EnvironmentObject var coordinator: AppCoordinator

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
                .padding(.horizontal, 8)
                .padding(.top, 4)
                .padding(.bottom, 8)

            Divider()

            peersSection
                .padding(.vertical, 6)

            Divider()

            VStack(alignment: .leading, spacing: 2) {
                MenuActionRow(icon: "gearshape", title: "Settings…") {
                    SettingsWindowController.shared.show(coordinator: coordinator)
                }
                MenuActionRow(icon: coordinator.globalPaused ? "play.fill" : "pause.fill",
                              title: coordinator.globalPaused ? "Resume syncing" : "Pause syncing") {
                    coordinator.setGlobalPause(!coordinator.globalPaused)
                }
                MenuActionRow(icon: "power", title: "Quit ClipSync") {
                    NSApp.terminate(nil)
                }
            }
            .padding(.vertical, 6)
        }
        .padding(6)
        .frame(width: 300)
    }

    // MARK: Header

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            Text("ClipSync")
                .font(.headline)
            if coordinator.globalPaused {
                Text("Paused")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 1)
                    .background(Color.orange, in: Capsule())
            }
            Spacer()
            Text("\(myFingerprint) / \(myVersion)")
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
        }
    }

    // MARK: Peers

    @ViewBuilder
    private var peersSection: some View {
        if coordinator.peerList.isEmpty {
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Looking for peers on the local network…")
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
        } else {
            VStack(spacing: 2) {
                ForEach(coordinator.peerList) { peer in
                    peerRow(peer)
                }
            }
        }
    }

    @ViewBuilder
    private func peerRow(_ peer: Peer) -> some View {
        let paused = coordinator.isPeerPaused(peer.didHex)
        HStack(spacing: 8) {
            Circle()
                .fill(dotColor(peer, paused: paused))
                .frame(width: 8, height: 8)
            VStack(alignment: .leading, spacing: 1) {
                Text(peer.name).fontWeight(.medium)
                subtitle(peer, paused: paused)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if peer.state == .pending {
                Button("Trust") { coordinator.trust(didHex: peer.didHex) }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
                    .accessibilityLabel("Trust \(peer.name)")
            } else {
                Button {
                    coordinator.setPeerPaused(peer.didHex, paused: !paused)
                } label: {
                    Image(systemName: paused ? "play.fill" : "pause.fill")
                        .font(.system(size: 10, weight: .bold))
                        .frame(width: 26, height: 20)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
                .help(paused ? "Resume syncing to \(peer.name)"
                             : "Pause syncing to \(peer.name)")
                .accessibilityLabel(paused ? "Resume syncing to \(peer.name)"
                                           : "Pause syncing to \(peer.name)")
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
    }

    /// The dot mirrors the state word: green when data can flow, orange
    /// when a mute is why it can't, yellow while still linking up, grey
    /// when off or not yet trusted.
    private func dotColor(_ peer: Peer, paused: Bool) -> Color {
        if paused && peer.state == .online { return .orange }
        switch peer.state {
        case .online:  return .green
        case .looking: return .yellow
        case .offline: return Color.gray.opacity(0.6)
        case .pending: return Color.gray
        }
    }

    private func subtitle(_ peer: Peer, paused: Bool) -> Text {
        // State, then the fingerprint (for eyeball-checking against the
        // other machine when pairing) and the version it reported, in the
        // same "<id> / <version>" shape as this Mac's header line. That half
        // uses the header's monospaced font; a peer that hasn't connected
        // has no version yet, so its version is dropped.
        let base = stateLabel(peer, paused: paused)
        var idVersion = peer.fingerprintShort
        if let v = peer.version { idVersion += " / \(v)" }
        return Text("\(base) • ").font(.caption)
            + Text(idVersion).font(.caption.monospaced())
    }

    private func stateLabel(_ peer: Peer, paused: Bool) -> String {
        // A muted online peer reads "Paused", because the reason nothing
        // reaches it is the mute, not the network.
        if paused && peer.state == .online { return "Paused" }
        switch peer.state {
        case .online:  return "Online"
        case .pending: return "Not trusted"
        case .looking: return "Looking…"
        case .offline: return "Offline"
        }
    }

    private var myFingerprint: String {
        String(Identity.shared.didHex.prefix(8))
    }

    private var myVersion: String { AppVersion.current }
}

/// A full-width action row with a leading icon that highlights on hover —
/// the popover analogue of a menu item. A plain Button can't hold the hover
/// state, so this wraps one.
private struct MenuActionRow: View {
    let icon: String
    let title: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon)
                    .frame(width: 18)
                    .foregroundStyle(.secondary)
                Text(title)
                Spacer()
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .contentShape(Rectangle())
            .background(hovering ? Color.primary.opacity(0.08) : .clear,
                        in: RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}
