import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject var coordinator: AppCoordinator

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("ClipSync").font(.headline)
                Spacer()
                Text(myFingerprint)
                    .font(.system(.caption, design: .monospaced))
                    .foregroundStyle(.secondary)
            }

            if coordinator.peerList.isEmpty {
                Text("Looking for peers on the local network…")
                    .foregroundStyle(.secondary).font(.callout)
            } else {
                Divider()
                ForEach(coordinator.peerList) { peer in
                    PeerRow(peer: peer) { coordinator.trust(didHex: peer.didHex) }
                }
            }

            Divider()
            Button("Quit ClipSync") { NSApp.terminate(nil) }
        }
        .padding(12)
        .frame(width: 320)
    }

    private var myFingerprint: String {
        "me: " + String(Identity.shared.didHex.prefix(8))
    }
}

private struct PeerRow: View {
    let peer: Peer
    let onTrust: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Circle()
                    .fill(color(for: peer.state))
                    .frame(width: 8, height: 8)
                Text(peer.name).fontWeight(.medium)
                Spacer()
                Text(stateLabel).foregroundStyle(.secondary).font(.caption)
            }
            HStack {
                Text(peer.fingerprintShort)
                    .font(.system(.caption, design: .monospaced))
                    .foregroundStyle(.secondary)
                Spacer()
                if peer.state == .pending {
                    Button("Trust", action: onTrust)
                        .buttonStyle(.borderedProminent)
                        .controlSize(.mini)
                }
            }
        }
        .padding(.vertical, 4)
    }

    private var stateLabel: String {
        switch peer.state {
        case .online: return "Online"
        case .pending: return "Not trusted"
        case .offline: return "Offline"
        }
    }

    private func color(for s: Peer.State) -> Color {
        switch s {
        case .online:  return .green
        case .pending: return .orange
        case .offline: return .gray
        }
    }
}
