import XCTest
import Network
@testable import ClipSync

/// The badge rule: only a device that has actually knocked — tried to
/// connect and been refused at the handshake — is waiting on an answer from
/// this user. A device merely seen advertising still gets a row and a Trust
/// button, but must not badge the menu bar, or on a shared network the badge
/// never clears and stops meaning anything.
final class PeerRegistryAttentionTests: XCTestCase {
    private let someone = "aabbccdd" + String(repeating: "0", count: 56)
    private let knocker = "11112222" + String(repeating: "0", count: 56)
    private let endpoint = NWEndpoint.hostPort(host: "127.0.0.1", port: 9999)

    /// The last list the registry published.
    private func capture(_ build: (PeerRegistry) -> Void) -> [Peer] {
        let registry = PeerRegistry()
        var last: [Peer] = []
        registry.onChange = { last = $0 }
        build(registry)
        return last
    }

    func testDiscoveredPeerIsPendingButHasNotKnocked() {
        let peers = capture {
            $0.notePending(name: "Someone's laptop", didHex: someone, endpoint: endpoint)
        }
        let peer = try? XCTUnwrap(peers.first)
        XCTAssertEqual(peers.count, 1)
        XCTAssertEqual(peer?.state, .pending, "it still gets a row and a Trust button")
        XCTAssertEqual(peer?.knocked, false, "but it has not asked us for anything")
    }

    func testRefusedPeerKnocked() {
        let peers = capture { $0.noteRefused(didHex: knocker) }
        XCTAssertEqual(peers.count, 1)
        XCTAssertEqual(peers.first?.state, .pending)
        XCTAssertEqual(peers.first?.knocked, true)
        XCTAssertEqual(peers.first?.name, String(knocker.prefix(8)),
                       "named by fingerprint — the advertised name may never arrive")
    }

    /// The reason the flag lives beside the pending map rather than in it: a
    /// discovery arriving after the knock replaces the entry to pick up the
    /// advertised name, and must not lose the knock with it.
    func testDiscoveryAfterAKnockKeepsTheKnockAndTakesTheName() {
        let peers = capture {
            $0.noteRefused(didHex: knocker)
            $0.notePending(name: "PCLARKE-WIN11-1", didHex: knocker, endpoint: endpoint)
        }
        XCTAssertEqual(peers.first?.name, "PCLARKE-WIN11-1")
        XCTAssertEqual(peers.first?.knocked, true)
    }

    /// And the other order: a peer already listed from discovery that then
    /// knocks must start raising the badge.
    func testKnockAfterDiscoveryStillCounts() {
        let peers = capture {
            $0.notePending(name: "PCLARKE-WIN11-1", didHex: knocker, endpoint: endpoint)
            $0.noteRefused(didHex: knocker)
        }
        XCTAssertEqual(peers.first?.name, "PCLARKE-WIN11-1", "the better name survives")
        XCTAssertEqual(peers.first?.knocked, true)
    }

    /// Clicking Trust answers the question, so the badge has to stop asking
    /// straight away — not when the connect finally lands.
    func testTrustingClearsTheKnock() {
        let peers = capture {
            $0.noteRefused(didHex: knocker)
            $0.trust(didHex: knocker)
        }
        XCTAssertEqual(peers.first?.knocked, false)
    }

    /// One quiet stranger must not mask a device that really is asking.
    func testAKnockerAmongDiscoveredPeersIsStillMarked() {
        let peers = capture {
            $0.notePending(name: "Someone's laptop", didHex: someone, endpoint: endpoint)
            $0.noteRefused(didHex: knocker)
        }
        XCTAssertEqual(peers.count, 2)
        XCTAssertEqual(peers.first { $0.didHex == someone }?.knocked, false)
        XCTAssertEqual(peers.first { $0.didHex == knocker }?.knocked, true)
    }
}
