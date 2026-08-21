import Foundation
import Network

struct Peer: Identifiable, Equatable {
    var id: String { didHex }
    let didHex: String
    let name: String
    let state: State
    /// App version a connected peer reported in its Hello; nil for peers
    /// that aren't connected or predate the field.
    var version: String? = nil
    /// First 8 hex chars of the SPKI fingerprint — shown in the TOFU
    /// pair UI so the user can eyeball-verify it matches on both sides.
    var fingerprintShort: String { String(didHex.prefix(8)) }
    /// looking: a trusted peer seen advertised but not yet connected, with
    /// no evidence it is unreachable — the honest label between discovery
    /// and the first Hello. Calling that window "offline" would report a
    /// guess as a fact for however long the connect takes.
    enum State: Equatable { case online, pending, looking, offline }

    func with(state: State) -> Peer {
        Peer(didHex: didHex, name: name, state: state, version: version)
    }
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
    /// Trusted peers seen advertised but not yet connected, shown as
    /// "Looking…". Keyed by didHex.
    private var looking: [String: Peer] = [:]
    /// When each was first seen this session; "Looking…" lapses to
    /// "Offline" LookingWindow after that if nothing connects.
    private var firstSeen: [String: Date] = [:]
    /// Peers whose connect attempt reached every advertised address and
    /// failed — evidence they are off. Cleared when a connection comes up.
    private var unreachable: Set<String> = []

    private static let lookingWindow: TimeInterval = 30

    // All state above is touched only on the main queue (Discovery runs
    // there and every connection callback is dispatched there), so no lock.

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
        // A live link is the strongest evidence of reachability.
        unreachable.remove(hex)
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

    /// A trusted peer has been discovered but is not yet connected. Show it
    /// as "Looking…" rather than omitting it, so a peer that exists but
    /// hasn't linked up is visible. Called each time it is seen advertised.
    func noteLooking(name: String, didHex: String) {
        let key = didHex.lowercased()
        guard connections[key] == nil else { return }
        looking[key] = Peer(didHex: key, name: name, state: .looking)
        if firstSeen[key] == nil {
            firstSeen[key] = Date()
            // Nothing else re-emits when the window lapses, so schedule the
            // flip to Offline; harmless if a connection arrives meanwhile.
            DispatchQueue.main.asyncAfter(deadline: .now() + Self.lookingWindow) { [weak self] in
                self?.emit()
            }
        }
        emit()
    }

    /// A connect attempt tried every advertised address and never reached
    /// the peer: real evidence it is off, so stop saying "Looking…".
    func markUnreachable(didHex: String) {
        let key = didHex.lowercased()
        guard connections[key] == nil else { return }
        if unreachable.insert(key).inserted { emit() }
    }

    /// State to display for a discovered, not-connected trusted peer:
    /// "Looking…" until either a connect fails outright or the window
    /// lapses, then "Offline". After that first window the state is known,
    /// so a peer that drops later reads "Offline" at once.
    private func resolveState(_ key: String, _ peer: Peer) -> Peer.State {
        guard peer.state == .looking else { return peer.state }
        if unreachable.contains(key) { return .offline }
        if let seen = firstSeen[key], Date().timeIntervalSince(seen) < Self.lookingWindow {
            return .looking
        }
        return .offline
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
        var shown = Set<String>()
        for (hex, pc) in connections {
            list.append(Peer(didHex: hex, name: pc.peerName ?? "Peer", state: .online,
                             version: pc.peerVersion))
            shown.insert(hex)
        }
        for (hex, entry) in pending where !shown.contains(hex) {
            list.append(entry.peer)
            shown.insert(hex)
        }
        for (hex, peer) in looking where !shown.contains(hex) {
            list.append(peer.with(state: resolveState(hex, peer)))
            shown.insert(hex)
        }
        onChange?(list)
    }
}
