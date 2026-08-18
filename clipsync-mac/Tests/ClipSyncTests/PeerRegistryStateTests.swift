import XCTest
@testable import ClipSync

/// The evidence-based part of the "Looking…" state machine — the parts
/// that don't depend on the 30 s time window, which is exercised visually.
@MainActor
final class PeerRegistryStateTests: XCTestCase {
    private let did = "aaaa111122223333"

    private func makeRegistry() -> (PeerRegistry, () -> [Peer]) {
        let registry = PeerRegistry()
        var last: [Peer] = []
        registry.onChange = { last = $0 }
        return (registry, { last })
    }

    func testDiscoveredTrustedPeerShowsAsLooking() {
        let (registry, latest) = makeRegistry()
        registry.noteLooking(name: "Peer", didHex: did)
        XCTAssertEqual(latest().first(where: { $0.didHex == did })?.state, .looking)
    }

    func testUnreachableFlipsLookingToOffline() {
        let (registry, latest) = makeRegistry()
        registry.noteLooking(name: "Peer", didHex: did)
        registry.markUnreachable(didHex: did)
        XCTAssertEqual(latest().first(where: { $0.didHex == did })?.state, .offline)
    }

    func testDidsAreComparedLowercase() {
        let (registry, latest) = makeRegistry()
        registry.noteLooking(name: "Peer", didHex: did.uppercased())
        registry.markUnreachable(didHex: did)   // lower-cased lookup still matches
        XCTAssertEqual(latest().first(where: { $0.didHex == did })?.state, .offline)
    }

    func testUntrustedPeerIsPendingNotLooking() {
        let (registry, latest) = makeRegistry()
        registry.notePending(name: "Stranger", didHex: did, endpoint: .hostPort(host: "127.0.0.1", port: 9))
        XCTAssertEqual(latest().first(where: { $0.didHex == did })?.state, .pending)
    }
}
