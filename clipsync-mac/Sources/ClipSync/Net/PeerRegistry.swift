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
        connections[hex] = pc
        pending.removeValue(forKey: hex)
        onPeerConnected?(hex, pc.peerName ?? "Peer")
        emit()
    }

    /// Fired when an mTLS connection completes Hello — Discovery uses
    /// this to promote the peer from the ephemeral pending set into the
    /// persistent TrustStore so future launches auto-connect.
    var onPeerConnected: ((String, String) -> Void)?

    private func unregister(_ pc: PeerConnection) {
        if let did = pc.peerDid {
            let hex = did.map { String(format: "%02x", $0) }.joined()
            connections.removeValue(forKey: hex)
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

    func broadcast(_ item: ClipboardItem) {
        for pc in connections.values { pc.send(item: item) }
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
