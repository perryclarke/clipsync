import Foundation
import Network

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

    private(set) var peerDid: Data?
    private(set) var peerName: String?

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
                self.sendHello()
                self.readLoop()
            case .failed, .cancelled:
                self.onClose?()
            default: break
            }
        }
        connection.start(queue: .main)
    }

    func send(_ frame: Data) {
        connection.send(content: frame, completion: .contentProcessed { _ in })
    }

    func send(item: ClipboardItem) { send(Codec.encodeClipboardItem(item)) }

    private func sendHello() {
        let f = Codec.encodeHello(did: identity.did, name: Host.current().localizedName ?? "Mac",
                                  caps: ["text", "image", "files", "rich"])
        send(f)
    }

    // MARK: - Framed read loop

    private var inbox = Data()

    private func readLoop() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, finished, error in
            guard let self else { return }
            if let data { self.inbox.append(data); self.drain() }
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
            if case let .map(m) = cbor,
               case let .byteString(did)? = m["did"] { self.peerDid = Data(did) }
            if case let .map(m) = cbor,
               case let .utf8String(n)? = m["name"] { self.peerName = n }
            onLog?("hello from \(peerName ?? "?") \(peerDid?.prefix(4).map { String(format: "%02x", $0) }.joined() ?? "?")")
            onReady?()
        case .clipboardItem:
            if let item = Codec.decodeClipboardItem(cbor) { onItem?(item) }
        default:
            break
        }
    }

    func close() { connection.cancel() }
}
