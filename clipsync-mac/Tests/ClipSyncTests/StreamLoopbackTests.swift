import XCTest
import Network
@testable import ClipSync

/// Streams a multi-megabyte item through two real PeerConnections over a
/// loopback mutual-TLS link and checks it arrives inline and hash-equal:
/// the planner/assembler pair, the codec, and the connection wiring,
/// together. Opt-in for the same reasons as PeerCertBindingTests (keychain
/// identity + loopback socket). Run with
/// `CLIPSYNC_TLS_LOOPBACK=1 swift test --filter StreamLoopback`.
final class StreamLoopbackTests: XCTestCase {

    func testLargeItemArrivesInlineOverLoopback() throws {
        guard ProcessInfo.processInfo.environment["CLIPSYNC_TLS_LOOPBACK"] == "1" else {
            throw XCTSkip("opt-in: needs keychain + loopback networking")
        }

        let identity = Identity.loadOrCreate()
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("trust-\(UUID().uuidString).plist")
        let trust = TrustStore(url: tmp, entries: [
            identity.didHex: TrustStore.Entry(didHex: identity.didHex, name: "self", addedAt: Date())
        ])

        var serverPC: PeerConnection?
        let serverReady = expectation(description: "server hello")
        let received = expectation(description: "server received item")
        var got: ClipboardItem?

        let listenerReady = expectation(description: "listener ready")
        let listener = try NWListener(using: NWParameters(
            tls: TLS.makeServerOptions(identity: identity, trustStore: trust)))
        listener.newConnectionHandler = { conn in
            let pc = PeerConnection(connection: conn, identity: identity, trustStore: trust, role: .server)
            pc.onLog = { NSLog("server: %@", $0) }
            pc.onReady = { serverReady.fulfill() }
            pc.onItem = { got = $0; received.fulfill() }
            serverPC = pc
            pc.start()
        }
        listener.stateUpdateHandler = { if case .ready = $0 { listenerReady.fulfill() } }
        listener.start(queue: .main)
        wait(for: [listenerReady], timeout: 10)
        let port = try XCTUnwrap(listener.port)

        let clientReady = expectation(description: "client hello")
        let client = PeerConnection(
            connection: NWConnection(to: .hostPort(host: "127.0.0.1", port: port),
                                     using: NWParameters(tls: TLS.makeClientOptions(identity: identity, trustStore: trust))),
            identity: identity, trustStore: trust, role: .client)
        client.onLog = { NSLog("client: %@", $0) }
        client.onReady = { clientReady.fulfill() }
        client.start()
        wait(for: [serverReady, clientReady], timeout: 15)

        // 5 MB image + small text: one streamed format, one inline.
        var png = Data(count: 5 * 1024 * 1024)
        for i in stride(from: 0, to: png.count, by: 4099) { png[i] = UInt8(truncatingIfNeeded: i) }
        let item = ClipboardItem(seq: 9, originDid: identity.did, tsMs: 1,
                                 formats: [ClipFormat(mime: "text/plain", size: 5, payload: .inline(Data("hello".utf8))),
                                           ClipFormat(mime: "image/png", size: UInt64(png.count), payload: .inline(png))],
                                 hint: "hello")
        client.send(item: item)
        wait(for: [received], timeout: 30)

        client.close()
        serverPC?.close()
        listener.cancel()

        let done = try XCTUnwrap(got)
        XCTAssertEqual(done.formats.count, 2)
        for f in done.formats {
            guard case .inline = f.payload else { return XCTFail("\(f.mime) arrived as a stream reference") }
        }
        XCTAssertEqual(done.canonicalHash, item.canonicalHash)
    }
}
