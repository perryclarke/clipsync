import XCTest
@testable import ClipSync

final class SyncPauseTests: XCTestCase {
    private var url: URL!
    private var settings: AppSettings!
    private var pause: SyncPause!

    private let peerA = "aaaa111122223333"
    private let peerB = "bbbb444455556666"

    override func setUp() {
        super.setUp()
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("clipsync-tests-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("settings.json")
        settings = AppSettings.load(url: url)
        pause = SyncPause(settings: settings)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
        super.tearDown()
    }

    func testDefaultIsSendToEveryone() {
        XCTAssertTrue(pause.shouldSend(to: peerA))
        XCTAssertTrue(pause.shouldSend(to: peerB))
    }

    func testGlobalPauseStopsEveryPeer() {
        pause.globalPaused = true
        XCTAssertFalse(pause.shouldSend(to: peerA))
        XCTAssertFalse(pause.shouldSend(to: peerB))
    }

    func testMuteStopsOnePeerAndLeavesTheOthers() {
        pause.setMuted(peerA, muted: true)
        XCTAssertFalse(pause.shouldSend(to: peerA))
        XCTAssertTrue(pause.shouldSend(to: peerB))
    }

    /// The two gates are independent: un-muting a peer while globally
    /// paused still sends nothing…
    func testUnmutingWhileGloballyPausedStillSendsNothing() {
        pause.setMuted(peerA, muted: true)
        pause.globalPaused = true
        pause.setMuted(peerA, muted: false)
        XCTAssertFalse(pause.shouldSend(to: peerA))
    }

    /// …and resuming globally leaves a muted peer muted.
    func testResumingGloballyLeavesMutedPeerMuted() {
        pause.setMuted(peerA, muted: true)
        pause.globalPaused = true
        pause.globalPaused = false
        XCTAssertFalse(pause.shouldSend(to: peerA))
        XCTAssertTrue(pause.shouldSend(to: peerB))
    }

    func testDidsCompareCaseInsensitively() {
        pause.setMuted(peerA.uppercased(), muted: true)
        XCTAssertTrue(pause.isMuted(peerA))
        XCTAssertFalse(pause.shouldSend(to: peerA.uppercased()))
    }

    /// The persistence asymmetry: a mute survives a relaunch, a global
    /// pause does not.
    func testMuteSurvivesReloadAndGlobalPauseDoesNot() {
        pause.setMuted(peerA, muted: true)
        pause.globalPaused = true

        let reloadedSettings = AppSettings.load(url: url)
        let reloadedPause = SyncPause(settings: reloadedSettings)
        XCTAssertFalse(reloadedPause.globalPaused)
        XCTAssertTrue(reloadedPause.isMuted(peerA))
        XCTAssertFalse(reloadedPause.shouldSend(to: peerA))
        XCTAssertTrue(reloadedPause.shouldSend(to: peerB))
    }

    func testMutingIsIdempotent() {
        pause.setMuted(peerA, muted: true)
        pause.setMuted(peerA, muted: true)
        XCTAssertEqual(pause.mutedPeers, [peerA])
        pause.setMuted(peerA, muted: false)
        pause.setMuted(peerA, muted: false)
        XCTAssertTrue(pause.mutedPeers.isEmpty)
    }

    func testBlankDidIsIgnored() {
        var changes = 0
        pause.onChange = { changes += 1 }
        pause.setMuted("  ", muted: true)
        XCTAssertTrue(pause.mutedPeers.isEmpty)
        XCTAssertEqual(changes, 0, "onChange must not fire for a no-op")
    }

    func testOnChangeFiresForRealChangesOnly() {
        var changes = 0
        pause.onChange = { changes += 1 }
        pause.globalPaused = true
        pause.globalPaused = true      // no-op
        pause.setMuted(peerA, muted: true)
        pause.setMuted(peerA, muted: true)  // no-op
        XCTAssertEqual(changes, 2)
    }

    /// Mutes round-trip alongside excluded apps in the shared file
    /// without either clobbering the other.
    func testMutesRoundTripAlongsideExcludedApps() {
        let app = AppIdentity(kind: .bundle, key: "com.example.app", displayName: "x")!
        settings.add(app)
        pause.setMuted(peerA, muted: true)
        settings.add(AppIdentity(kind: .bundle, key: "com.example.two", displayName: "y")!)

        let reloaded = AppSettings.load(url: url)
        XCTAssertEqual(reloaded.excluded.count, 2)
        XCTAssertEqual(reloaded.pausedPeers, [peerA])
    }
}
