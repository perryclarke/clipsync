import Foundation
import Network

/// mDNS advertise + browse for `_clipsync._tcp`, and handing
/// connections off to PeerConnection.
final class Discovery {
    private let identity: Identity
    private let trustStore: TrustStore
    private let peers: PeerRegistry

    private var listener: NWListener?
    private var browser: NWBrowser?

    /// Fired for every diagnostic event so the UI (and NSLog) can
    /// surface what discovery is actually doing.
    var onLog: ((String) -> Void)?

    init(identity: Identity, trustStore: TrustStore, peers: PeerRegistry) {
        self.identity = identity
        self.trustStore = trustStore
        self.peers = peers
    }

    func start() {
        peers.onTrustRequested = { [weak self] didHex, name, endpoint in
            guard let self else { return }
            self.log("trust \(name) \(didHex.prefix(8))")
            self.trustStore.add(didHex: didHex, name: name)
            self.connect(to: endpoint)
        }
        peers.onPeerConnected = { [weak self] didHex, name in
            guard let self else { return }
            if !self.trustStore.contains(hex: didHex) {
                self.log("promoting \(name) \(didHex.prefix(8)) to persistent trust")
                self.trustStore.add(didHex: didHex, name: name)
            }
        }
        startListener()
        startBrowser()
    }

    private func log(_ s: String) {
        NSLog("ClipSync: %@", s)
        onLog?(s)
    }

    private func startListener() {
        let tlsOptions = TLS.makeServerOptions(identity: identity, trustStore: trustStore)
        let params = NWParameters(tls: tlsOptions)
        var txtDict: [String: String] = [
            "v": "1",
            "did": identity.didHex,
            "name": Host.current().localizedName ?? "Mac",
            "caps": "text,image,files,rich",
            "pend": trustStore.isEmpty ? "1" : "0"
        ]
        let txt = NWTXTRecord(txtDict)
        _ = txtDict

        do {
            let l = try NWListener(using: params)
            l.service = NWListener.Service(
                name: Host.current().localizedName,
                type: "_clipsync._tcp",
                domain: nil,
                txtRecord: txt
            )
            l.stateUpdateHandler = { [weak self] state in
                self?.log("listener state: \(state)")
            }
            l.serviceRegistrationUpdateHandler = { [weak self] change in
                self?.log("service registration: \(change)")
            }
            l.newConnectionHandler = { [weak self] conn in
                guard let self else { return }
                self.log("incoming connection from \(conn.endpoint)")
                let pc = PeerConnection(connection: conn, identity: self.identity,
                                        trustStore: self.trustStore, role: .server)
                pc.onLog = { [weak self] in self?.log($0) }
                self.peers.adopt(pc)
                pc.start()
            }
            l.start(queue: .main)
            self.listener = l
            log("listener started on port \(l.port?.rawValue ?? 0)")
        } catch {
            log("listener failed: \(error)")
        }
    }

    private func startBrowser() {
        let params = NWParameters()
        let b = NWBrowser(for: .bonjourWithTXTRecord(type: "_clipsync._tcp", domain: nil), using: params)
        b.stateUpdateHandler = { [weak self] state in
            self?.log("browser state: \(state)")
        }
        b.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self else { return }
            self.log("browse results: \(results.count)")
            for result in results {
                self.handleResult(result)
            }
        }
        b.start(queue: .main)
        self.browser = b
        log("browser started")
    }

    private func handleResult(_ result: NWBrowser.Result) {
        let endpoint = result.endpoint
        var serviceName = "?"
        if case let .service(name, _, _, _) = endpoint { serviceName = name }

        var didHex: String? = nil
        var peerName = serviceName
        if case let .bonjour(txt) = result.metadata {
            didHex = txt["did"]
            if let n = txt["name"] { peerName = n }
        }

        log("peer \(serviceName) did=\(didHex?.prefix(8) ?? "nil")")

        guard let peerDid = didHex else { return }
        if peerDid == identity.didHex { return }    // self

        if trustStore.contains(hex: peerDid) {
            if !peers.isConnected(didHex: peerDid) {
                connect(to: endpoint)
            }
        } else {
            peers.notePending(name: peerName, didHex: peerDid, endpoint: endpoint)
        }
    }

    private func connect(to endpoint: NWEndpoint) {
        log("connecting to \(endpoint)")
        let tlsOptions = TLS.makeClientOptions(identity: identity, trustStore: trustStore)
        let params = NWParameters(tls: tlsOptions)
        let conn = NWConnection(to: endpoint, using: params)
        let pc = PeerConnection(connection: conn, identity: identity,
                                trustStore: trustStore, role: .client)
        pc.onLog = { [weak self] in self?.log($0) }
        peers.adopt(pc)
        pc.start()
    }
}
