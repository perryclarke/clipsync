import Foundation
import Network
import Security

enum PeerRole { case client, server }

final class PeerConnection {
    let connection: NWConnection
    let role: PeerRole
    private let identity: Identity
    private let trustStore: TrustStore

    var onReady: (() -> Void)?
    var onItem: ((ClipboardItem) -> Void)?
    var onClose: (() -> Void)?
    var onLog: ((String) -> Void)?

    /// The peer's authoritative identity: the SHA-256 of the SPKI of the
    /// certificate it actually presented in the mTLS handshake, which the
    /// verify block already pinned against the trust store. Derived from
    /// the connection's own negotiated TLS metadata at `.ready`, NOT from
    /// the `did` a peer claims in its Hello — a trusted-but-malicious peer
    /// could otherwise assert any did and dodge a per-peer mute, since the
    /// registry and SyncPause key off this value.
    private(set) var peerDid: Data?
    private(set) var peerName: String?

    private var keepaliveTimer: Timer?
    private var lastReceive = Date()
    /// Whether the TLS handshake ever completed. Lets a caller tell a
    /// connect attempt that never reached the peer (→ unreachable) from a
    /// live link that later dropped.
    private(set) var becameReady = false

    private static let pingInterval: TimeInterval = 20
    private static let idleTimeout: TimeInterval = 75
    private static let readyTimeout: TimeInterval = 15

    init(connection: NWConnection, identity: Identity, trustStore: TrustStore, role: PeerRole) {
        self.connection = connection
        self.identity = identity
        self.trustStore = trustStore
        self.role = role
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            self.onLog?("conn \(self.role) state \(state)")
            switch state {
            case .ready:
                self.becameReady = true
                self.lastReceive = Date()
                // Bind the peer's identity to its verified certificate
                // before any Hello is read, so the claimed did can never
                // be the identity we act on. Fail closed: if the cert
                // can't be read (should be impossible after a successful
                // pinned handshake), drop the connection rather than fall
                // back to a self-asserted did.
                guard self.capturePeerDid() else {
                    self.onLog?("conn \(self.role): no verified peer cert, closing")
                    self.connection.cancel()
                    return
                }
                self.sendHello()
                self.readLoop()
                self.startKeepalive()
            case .failed, .cancelled:
                self.keepaliveTimer?.invalidate()
                self.keepaliveTimer = nil
                self.onClose?()
            default: break
            }
        }
        connection.start(queue: .main)

        // Give up on connections that never become ready (stale address,
        // peer asleep) so discovery can retry with a fresh endpoint.
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.readyTimeout) { [weak self] in
            guard let self, !self.becameReady else { return }
            self.onLog?("conn \(self.role) timed out before ready")
            self.connection.cancel()
        }
    }

    func send(_ frame: Data) {
        connection.send(content: frame, completion: .contentProcessed { _ in })
    }

    func send(item: ClipboardItem) { send(Codec.encodeClipboardItem(item)) }

    /// Read the peer's leaf certificate from this connection's own
    /// negotiated TLS metadata and set `peerDid` to its SPKI hash. Uses
    /// per-connection metadata rather than the shared verify block, whose
    /// closure the server reuses across every incoming connection. Returns
    /// false if no peer certificate is available.
    private func capturePeerDid() -> Bool {
        guard let tls = connection.metadata(definition: NWProtocolTLS.definition)
                as? NWProtocolTLS.Metadata else { return false }
        var leaf: SecCertificate?
        let ok = sec_protocol_metadata_access_peer_certificate_chain(
            tls.securityProtocolMetadata
        ) { cert in
            if leaf == nil { leaf = sec_certificate_copy_ref(cert).takeRetainedValue() }
        }
        guard ok, let leaf else { return false }
        peerDid = TrustStore.spkiSha256(of: leaf)
        return true
    }

    private func sendHello() {
        let f = Codec.encodeHello(did: identity.did, name: Host.current().localizedName ?? "Mac",
                                  caps: ["text", "image", "files", "rich"])
        send(f)
    }

    /// App-level keepalive: detects dead links (sleep, AP roam) that TCP
    /// alone takes minutes to notice, so the registry stays accurate and
    /// the reconnect path can kick in.
    private func startKeepalive() {
        keepaliveTimer?.invalidate()
        keepaliveTimer = Timer.scheduledTimer(withTimeInterval: Self.pingInterval, repeats: true) { [weak self] _ in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastReceive) > Self.idleTimeout {
                self.onLog?("keepalive timeout, closing \(self.peerName ?? "?")")
                self.close()
                return
            }
            self.send(Codec.encodePing())
        }
    }

    // MARK: - Framed read loop

    private var inbox = Data()

    private func readLoop() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, finished, error in
            guard let self else { return }
            if let data {
                self.lastReceive = Date()
                self.inbox.append(data)
                self.drain()
            }
            if error != nil || finished { self.connection.cancel(); return }
            self.readLoop()
        }
    }

    private func drain() {
        while inbox.count >= 4 {
            let len = inbox.prefix(4).withUnsafeBytes { raw -> UInt32 in
                raw.load(as: UInt32.self).bigEndian
            }
            if Int(len) > Codec.maxFrameSize {
                connection.cancel(); return
            }
            guard inbox.count >= 4 + Int(len) else { return }
            let payload = inbox.subdata(in: 4 ..< 4 + Int(len))
            inbox.removeSubrange(0 ..< 4 + Int(len))
            handle(payload)
        }
    }

    private func handle(_ payload: Data) {
        guard let cbor = try? Codec.decodeFrame(payload),
              let type = Codec.messageType(cbor) else { return }
        switch type {
        case .hello:
            // `peerDid` is already bound to the verified cert (see
            // capturePeerDid); the Hello's `did` is informational only.
            // A mismatch means the peer claimed an identity that isn't its
            // certificate — a spoof attempt or a bug — so log it, but the
            // identity we act on stays the cert-derived one either way.
            if case let .map(m) = cbor,
               case let .byteString(claimed)? = m["did"],
               let verified = self.peerDid, Data(claimed) != verified {
                onLog?("hello did mismatch: claimed \(Data(claimed).prefix(4).map { String(format: "%02x", $0) }.joined()) != cert \(verified.prefix(4).map { String(format: "%02x", $0) }.joined())")
            }
            if case let .map(m) = cbor,
               case let .utf8String(n)? = m["name"] { self.peerName = n }
            onLog?("hello from \(peerName ?? "?") \(peerDid?.prefix(4).map { String(format: "%02x", $0) }.joined() ?? "?")")
            onReady?()
        case .clipboardItem:
            if let item = Codec.decodeClipboardItem(cbor) { onItem?(item) }
        case .ping:
            send(Codec.encodePong())
        case .pong:
            break
        default:
            break
        }
    }

    func close() {
        keepaliveTimer?.invalidate()
        keepaliveTimer = nil
        connection.cancel()
    }
}
