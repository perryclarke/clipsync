import XCTest
import Network
import Security
@testable import ClipSync

/// Proves the mechanism `PeerConnection.capturePeerDid()` relies on: after
/// a real mutual-TLS 1.3 handshake, the peer's leaf certificate can be read
/// from the connection's own negotiated metadata, and its SPKI hash equals
/// the pinned identity — i.e. the value the verify block authorized. This
/// is what makes per-peer mute un-spoofable: the identity comes from the
/// cert, not the Hello.
///
/// Opt-in (needs keychain access to present the local identity, and a
/// loopback socket), so it is skipped in the normal `swift test` run. Run
/// with `CLIPSYNC_TLS_LOOPBACK=1 swift test --filter PeerCertBinding`.
final class PeerCertBindingTests: XCTestCase {

    func testPeerDidBindsToVerifiedCertOverLoopback() throws {
        guard ProcessInfo.processInfo.environment["CLIPSYNC_TLS_LOOPBACK"] == "1" else {
            throw XCTSkip("opt-in: needs keychain + loopback networking")
        }

        let identity = Identity.loadOrCreate()
        // A trust store that pins our own cert, so a loopback client and
        // server (both presenting this identity) mutually verify.
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("trust-\(UUID().uuidString).plist")
        let trust = TrustStore(url: tmp, entries: [
            identity.didHex: TrustStore.Entry(didHex: identity.didHex, name: "self", addedAt: Date())
        ])

        let listenerReady = expectation(description: "listener ready")
        let listener = try NWListener(using: NWParameters(
            tls: TLS.makeServerOptions(identity: identity, trustStore: trust)))
        listener.newConnectionHandler = { conn in
            conn.stateUpdateHandler = { _ in }
            conn.start(queue: .main)
        }
        listener.stateUpdateHandler = { state in
            if case .ready = state { listenerReady.fulfill() }
        }
        listener.start(queue: .main)
        wait(for: [listenerReady], timeout: 10)
        let port = try XCTUnwrap(listener.port)

        let handshook = expectation(description: "client ready, cert extracted")
        var extracted: Data?
        let client = NWConnection(
            to: .hostPort(host: "127.0.0.1", port: port),
            using: NWParameters(tls: TLS.makeClientOptions(identity: identity, trustStore: trust)))
        client.stateUpdateHandler = { state in
            guard case .ready = state else { return }
            if let tls = client.metadata(definition: NWProtocolTLS.definition) as? NWProtocolTLS.Metadata {
                var leaf: SecCertificate?
                _ = sec_protocol_metadata_access_peer_certificate_chain(
                    tls.securityProtocolMetadata
                ) { cert in
                    if leaf == nil { leaf = sec_certificate_copy_ref(cert).takeRetainedValue() }
                }
                if let leaf { extracted = TrustStore.spkiSha256(of: leaf) }
            }
            handshook.fulfill()
        }
        client.start(queue: .main)
        wait(for: [handshook], timeout: 10)

        client.cancel()
        listener.cancel()

        XCTAssertEqual(extracted, identity.did,
                       "peer cert SPKI read from TLS metadata must equal the pinned identity did")
    }
}
