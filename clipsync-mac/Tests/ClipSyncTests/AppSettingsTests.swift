import XCTest
@testable import ClipSync

final class AppSettingsTests: XCTestCase {
    private var url: URL!

    override func setUp() {
        super.setUp()
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("clipsync-tests-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("settings.json")
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: url.deletingLastPathComponent())
        super.tearDown()
    }

    private func write(_ json: String) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                withIntermediateDirectories: true)
        try json.data(using: .utf8)!.write(to: url)
    }

    func testMissingFileYieldsEmptyDefaults() {
        let s = AppSettings.load(url: url)
        XCTAssertTrue(s.excluded.isEmpty)
        XCTAssertTrue(s.pausedPeers.isEmpty)
    }

    func testRoundTrip() {
        let s = AppSettings.load(url: url)
        let app = AppIdentity(kind: .bundle, key: "com.apple.Notes",
                              displayName: "Notes", path: "/System/Applications/Notes.app")!
        s.add(app)
        s.setPeerPaused("ABCDEF0123", paused: true)

        let reloaded = AppSettings.load(url: url)
        XCTAssertEqual(reloaded.excluded, [app])
        XCTAssertEqual(reloaded.excluded.first?.displayName, "Notes")
        XCTAssertEqual(reloaded.excluded.first?.path, "/System/Applications/Notes.app")
        XCTAssertEqual(reloaded.pausedPeers, ["abcdef0123"])
    }

    func testCorruptFileYieldsEmptyList() throws {
        try write("{ this is not json")
        let s = AppSettings.load(url: url)
        XCTAssertTrue(s.excluded.isEmpty)
        XCTAssertTrue(s.pausedPeers.isEmpty)
        XCTAssertTrue(s.hidden.isEmpty)
    }

    private let did = "b6bf89d9aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

    func testHiddenPeerSurvivesRoundTripAndMatchesCaseInsensitively() {
        AppSettings.load(url: url).hide(did.uppercased(), name: "Stranger")

        let reloaded = AppSettings.load(url: url)
        XCTAssertTrue(reloaded.isHidden(did))
        XCTAssertEqual(reloaded.hidden, [HiddenPeer(didHex: did, name: "Stranger")])
    }

    func testHideIsIdempotentAndKeepsTheFirstName() {
        let s = AppSettings.load(url: url)
        s.hide(did, name: "First")
        s.hide(did, name: "Second")
        XCTAssertEqual(s.hidden.map(\.name), ["First"])
    }

    func testUnhideRemovesTheEntryAndPersists() {
        let s = AppSettings.load(url: url)
        s.hide(did, name: "Stranger")
        s.unhide(did)

        XCTAssertFalse(s.isHidden(did))
        XCTAssertTrue(AppSettings.load(url: url).hidden.isEmpty)
    }

    func testHidingWithABlankNameFallsBackToTheFingerprint() {
        let s = AppSettings.load(url: url)
        s.hide(did, name: "  ")
        XCTAssertEqual(s.hidden.map(\.name), [String(did.prefix(8))])
    }

    func testResetAllEmptiesEverythingAndPersists() {
        let s = AppSettings.load(url: url)
        s.add(AppIdentity(kind: .bundle, key: "com.apple.Notes", displayName: "Notes")!)
        s.setPeerPaused(did, paused: true)
        s.hide(did, name: "Stranger")

        s.resetAll()

        XCTAssertTrue(s.excluded.isEmpty)
        XCTAssertTrue(s.pausedPeers.isEmpty)
        XCTAssertTrue(s.hidden.isEmpty)
        let reloaded = AppSettings.load(url: url)
        XCTAssertTrue(reloaded.excluded.isEmpty)
        XCTAssertTrue(reloaded.pausedPeers.isEmpty)
        XCTAssertTrue(reloaded.hidden.isEmpty)
    }

    /// The mirror of the Windows test that ignores `kind: "bundle"`: a
    /// file written by Windows loads here with its exe/package entries
    /// skipped, not treated as errors.
    func testUnknownKindsAreIgnored() throws {
        try write("""
        {
          "version": 1,
          "excludedApps": [
            { "kind": "exe", "key": "keepassxc.exe", "name": "KeePassXC" },
            { "kind": "package", "key": "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "name": "Windows Terminal" },
            { "kind": "bundle", "key": "com.apple.Notes", "name": "Notes" },
            { "kind": "hologram", "key": "future-thing", "name": "From the future" }
          ],
          "pausedPeers": []
        }
        """)
        let s = AppSettings.load(url: url)
        XCTAssertEqual(s.excluded.map(\.key), ["com.apple.notes"])
    }

    func testFileMissingVersionStillLoads() throws {
        try write("""
        { "excludedApps": [ { "kind": "bundle", "key": "com.apple.Notes", "name": "Notes" } ] }
        """)
        let s = AppSettings.load(url: url)
        XCTAssertEqual(s.excluded.count, 1)
    }

    func testAddAndRemoveAreIdempotent() {
        let s = AppSettings.load(url: url)
        let app = AppIdentity(kind: .bundle, key: "com.example.app", displayName: "x")!
        s.add(app)
        s.add(app)
        XCTAssertEqual(s.excluded.count, 1)
        s.remove(app)
        s.remove(app)
        XCTAssertTrue(s.excluded.isEmpty)
    }

    func testIsExcludedMatchesByKeyNotPresentation() {
        let s = AppSettings.load(url: url)
        s.add(AppIdentity(kind: .bundle, key: "com.example.app", displayName: "Original",
                          path: "/Applications/Original.app")!)
        let probe = AppIdentity(kind: .bundle, key: "COM.EXAMPLE.APP", displayName: "Different")!
        XCTAssertTrue(s.isExcluded(probe))
    }

    func testPausedPeersNormaliseDedupeAndDropBlanks() throws {
        try write("""
        { "version": 1, "excludedApps": [], "pausedPeers": ["ABC123", "abc123", "  ", "def456"] }
        """)
        let s = AppSettings.load(url: url)
        XCTAssertEqual(s.pausedPeers, ["abc123", "def456"])
    }

    /// An entry naming a peer this device has never met is harmless and
    /// kept, so pausing a machine, forgetting it, and meeting it again
    /// does not silently un-pause it.
    func testUnknownPeerEntrySurvivesRewrite() throws {
        try write("""
        { "version": 1, "excludedApps": [], "pausedPeers": ["neverseen"] }
        """)
        let s = AppSettings.load(url: url)
        // Trigger a persist via an unrelated change.
        s.add(AppIdentity(kind: .bundle, key: "com.example.app", displayName: "x")!)
        let reloaded = AppSettings.load(url: url)
        XCTAssertEqual(reloaded.pausedPeers, ["neverseen"])
    }

    func testBlankPeerIsIgnored() {
        let s = AppSettings.load(url: url)
        s.setPeerPaused("   ", paused: true)
        XCTAssertTrue(s.pausedPeers.isEmpty)
        XCTAssertFalse(s.isPeerPaused("   "))
    }

    /// The two features share the file; neither may clobber the other on
    /// write, in either order.
    func testFeaturesDoNotClobberEachOther() {
        let s = AppSettings.load(url: url)
        let app = AppIdentity(kind: .bundle, key: "com.example.app", displayName: "x")!

        s.add(app)
        s.setPeerPaused("abc123", paused: true)
        var reloaded = AppSettings.load(url: url)
        XCTAssertEqual(reloaded.excluded, [app])
        XCTAssertEqual(reloaded.pausedPeers, ["abc123"])

        // A pause-side write keeps the exclusions…
        reloaded.setPeerPaused("def456", paused: true)
        reloaded = AppSettings.load(url: url)
        XCTAssertEqual(reloaded.excluded, [app])
        XCTAssertEqual(Set(reloaded.pausedPeers), ["abc123", "def456"])

        // …and an exclusion-side write keeps the pauses.
        reloaded.remove(app)
        reloaded = AppSettings.load(url: url)
        XCTAssertTrue(reloaded.excluded.isEmpty)
        XCTAssertEqual(Set(reloaded.pausedPeers), ["abc123", "def456"])
    }
}
