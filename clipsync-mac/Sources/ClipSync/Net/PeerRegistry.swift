import Foundation
import Network

struct Peer: Identifiable, Equatable {
    var id: String { didHex }
    let didHex: String
    let name: String
    let state: State
    /// First 8 hex chars of the SPKI fingerprint — shown in the TOFU
    /// pair UI so the user can eyeball-verify it matches on both sides.
    var fingerprintShort: String { String(didHex.prefix(8)) }
    enum State: Equatable { case online, pending, offline }
}

final class PeerRegistry {
    var onRemoteItem: ((ClipboardItem) -> Void)?
    var onChange: (([Peer]) -> Void)?
    /// Fired when the user clicks Trust on a pending peer. Discovery
    /// wires this up to persist to the trust store and kick off a
    /// real mTLS connect using the saved endpoint.
    var onTrustRequested: ((String, String, NWEndpoint) -> Void)?
    /// Our own DID hex, used to break ties when both sides connect at once.
    var localDidHex: String = ""

    private var connections: [String: PeerConnection] = [:]       // didHex → live mTLS conn
    private var pending: [String: (peer: Peer, endpoint: NWEndpoint)] = [:]

    func adopt(_ pc: PeerConnection) {
        pc.onItem = { [weak self] in self?.onRemoteItem?($0) }
        pc.onReady = { [weak self] in self?.registerReady(pc) }
        pc.onClose = { [weak self] in self?.unregister(pc) }
    }

    private func registerReady(_ pc: PeerConnection) {
        guard let did = pc.peerDid else { return }
        let hex = did.map { String(format: "%02x", $0) }.joined()
        if let existing = connections[hex], existing !== pc {
            // Simultaneous connect from both sides. Both ends must keep the
            // *same* connection, so tie-break deterministically: keep the
            // one where the lower-DID device is the TLS client.
            let keepClient = localDidHex < hex
            if (pc.role == .client) == keepClient {
                connections[hex] = pc
                existing.close()
            } else {
                pc.close()
                return
            }
        } else {
            connections[hex] = pc
        }
        pending.removeValue(forKey: hex)
        emit()
    }

    private func unregister(_ pc: PeerConnection) {
        if let did = pc.peerDid {
            let hex = did.map { String(format: "%02x", $0) }.joined()
            // Only remove if this pc is still the registered connection —
            // a replaced duplicate must not evict its successor.
            if connections[hex] === pc {
                connections.removeValue(forKey: hex)
            }
        }
        emit()
    }

    func isConnected(didHex: String) -> Bool { connections[didHex] != nil }

    func notePending(name: String, didHex: String?, endpoint: NWEndpoint) {
        guard let didHex, connections[didHex] == nil else { return }
        pending[didHex] = (
            Peer(didHex: didHex, name: name, state: .pending),
            endpoint
        )
        emit()
    }

    /// Called from the menu-bar "Trust" button.
    func trust(didHex: String) {
        guard let entry = pending[didHex] else { return }
        onTrustRequested?(didHex, entry.peer.name, entry.endpoint)
    }

    /// Consulted for each peer before sending. Nil means send to
    /// everyone, which is what this class did before pausing existed and
    /// what it still does if nobody wires it up. Keeping the decision
    /// outside means the registry never learns what a pause is, and the
    /// global and per-peer cases both arrive through one predicate.
    var shouldSendTo: ((String) -> Bool)?

    func broadcast(_ item: ClipboardItem) {
        for (hex, pc) in connections {
            // Both branches log. A skip needs saying, or a user watches
            // nothing arrive with no way to tell a pause from a broken
            // link; and a send needs saying too, or the absence of a skip
            // line is indistinguishable from the item never reaching this
            // method.
            if let gate = shouldSendTo, !gate(hex) {
                NSLog("Broadcast: not sending to %@ (paused)", String(hex.prefix(8)))
                continue
            }
            NSLog("Broadcast: sending to %@", String(hex.prefix(8)))
            pc.send(item: item)
        }
    }

    private func emit() {
        var list: [Peer] = []
        for (hex, pc) in connections {
            list.append(Peer(didHex: hex, name: pc.peerName ?? "Peer", state: .online))
        }
        for (_, entry) in pending where connections[entry.peer.didHex] == nil {
            list.append(entry.peer)
        }
        onChange?(list)
    }
}
