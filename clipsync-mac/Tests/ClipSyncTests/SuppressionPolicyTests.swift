import XCTest
@testable import ClipSync

final class SuppressionPolicyTests: XCTestCase {
    private var settingsURL: URL!
    private var settings: AppSettings!
    private let t0 = Date(timeIntervalSinceReferenceDate: 2_000_000)

    override func setUp() {
        super.setUp()
        settingsURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("clipsync-tests-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("settings.json")
        settings = AppSettings.load(url: settingsURL)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: settingsURL.deletingLastPathComponent())
        super.tearDown()
    }

    private func app(_ key: String) -> AppIdentity {
        AppIdentity(kind: .bundle, key: key, displayName: key)!
    }

    private func t(_ seconds: TimeInterval) -> Date { t0.addingTimeInterval(seconds) }

    /// The fail-open branch, explicitly: nothing known about the window
    /// means transmit — never suppress on uncertainty.
    func testEmptyRingFailsOpen() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertFalse(d.suppress)
        XCTAssertNil(d.source)
    }

    func testUnresolvedForegroundFailsOpen() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: nil)
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertFalse(d.suppress)
    }

    func testExcludedAppFrontmostSuppresses() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: app("com.example.secret"))
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertTrue(d.suppress)
        XCTAssertEqual(d.source?.key, "com.example.secret")
    }

    func testNonExcludedAppTransmits() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: app("com.example.innocent"))
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertFalse(d.suppress)
    }

    /// The macOS polling-window rule: the copy could have happened at any
    /// point in the window, so an excluded app that held focus for *part*
    /// of it suppresses — even though a different app is frontmost at
    /// tick time. This is the fast copy-then-switch case.
    func testExcludedAppForPartOfWindowSuppresses() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: app("com.example.secret"))
        ring.record(at: t(0.1), app: app("com.example.innocent"))   // switched mid-window
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertTrue(d.suppress)
        XCTAssertEqual(d.source?.key, "com.example.secret")
    }

    /// The mirror: switching *into* an excluded app during the window
    /// also suppresses. Deliberately stricter than Windows.
    func testSwitchingIntoExcludedAppDuringWindowSuppresses() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: app("com.example.innocent"))
        ring.record(at: t(0.1), app: app("com.example.secret"))
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertTrue(d.suppress)
    }

    func testExcludedAppOutsideWindowDoesNotSuppress() {
        settings.add(app("com.example.secret"))
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: app("com.example.secret"))
        ring.record(at: t(-5), app: app("com.example.innocent"))    // switched before window
        let d = SuppressionPolicy.decide(ring: ring, settings: settings,
                                         windowStart: t(0), windowEnd: t(0.2))
        XCTAssertFalse(d.suppress)
    }

    func testRemovingExclusionTakesEffectImmediately() {
        let secret = app("com.example.secret")
        settings.add(secret)
        let ring = ForegroundRing()
        ring.record(at: t(-10), app: secret)
        XCTAssertTrue(SuppressionPolicy.decide(ring: ring, settings: settings,
                                               windowStart: t(0), windowEnd: t(0.2)).suppress)
        settings.remove(secret)
        XCTAssertFalse(SuppressionPolicy.decide(ring: ring, settings: settings,
                                                windowStart: t(0), windowEnd: t(0.2)).suppress)
    }
}
