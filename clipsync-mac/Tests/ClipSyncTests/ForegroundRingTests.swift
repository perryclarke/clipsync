import XCTest
@testable import ClipSync

final class ForegroundRingTests: XCTestCase {
    private let t0 = Date(timeIntervalSinceReferenceDate: 1_000_000)

    private func app(_ key: String) -> AppIdentity {
        AppIdentity(kind: .bundle, key: key, displayName: key)!
    }

    private func t(_ seconds: TimeInterval) -> Date { t0.addingTimeInterval(seconds) }

    // MARK: appAt

    func testQueryBeforeAnyTransitionIsUnknown() {
        let ring = ForegroundRing()
        XCTAssertNil(ring.appAt(t(0)))
        ring.record(at: t(10), app: app("com.example.a"))
        XCTAssertNil(ring.appAt(t(9)))
    }

    func testQueryInsideAnIntervalReturnsThatIntervalsApp() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(10), app: app("com.example.b"))
        XCTAssertEqual(ring.appAt(t(5))?.key, "com.example.a")
        XCTAssertEqual(ring.appAt(t(15))?.key, "com.example.b")
    }

    /// Half-open intervals: a timestamp exactly on a transition resolves
    /// to the newly-activated app, not the one it replaced.
    func testTimestampExactlyOnTransitionResolvesToNewApp() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(10), app: app("com.example.b"))
        XCTAssertEqual(ring.appAt(t(10))?.key, "com.example.b")
    }

    func testNewestEntryRunsToInfinity() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        XCTAssertEqual(ring.appAt(t(10_000))?.key, "com.example.a")
    }

    func testEvictionByAgeReturnsUnknown() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        // A recording 3 minutes later evicts the first entry (2-minute cap)…
        ring.record(at: t(180), app: app("com.example.b"))
        XCTAssertNil(ring.appAt(t(5)))
        // …but the newest entry always survives, however old.
        XCTAssertEqual(ring.appAt(t(180))?.key, "com.example.b")
    }

    func testCapAt16Entries() {
        let ring = ForegroundRing()
        for i in 0..<20 {
            ring.record(at: t(Double(i)), app: app("com.example.app\(i)"))
        }
        // The first four were dropped; a query in their range resolves to
        // nothing (predates the oldest retained entry).
        XCTAssertNil(ring.appAt(t(3.5)))
        XCTAssertEqual(ring.appAt(t(4))?.key, "com.example.app4")
        XCTAssertEqual(ring.appAt(t(19))?.key, "com.example.app19")
    }

    func testUnresolvedEntryReadsBackAsNil() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(10), app: nil)
        XCTAssertNil(ring.appAt(t(15)))
    }

    // MARK: appsIn — the polling-window query (handoff §3)

    func testAppHoldingFocusForPartOfWindowIsFound() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(5), app: app("com.example.b"))
        // Window (4.9, 5.1]: a held focus until 5, b from 5 on — both count.
        let found = ring.appsIn(start: t(4.9), end: t(5.1))
        XCTAssertEqual(found.map(\.key), ["com.example.a", "com.example.b"])
    }

    func testAppWhoseIntervalEndedBeforeWindowIsNotFound() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(5), app: app("com.example.b"))
        let found = ring.appsIn(start: t(6), end: t(7))
        XCTAssertEqual(found.map(\.key), ["com.example.b"])
    }

    func testWindowEntirelyBeforeRetainedHistoryIsEmpty() {
        let ring = ForegroundRing()
        ring.record(at: t(100), app: app("com.example.a"))
        XCTAssertTrue(ring.appsIn(start: t(0), end: t(50)).isEmpty)
    }

    func testUnresolvedEntriesContributeNothingToWindow() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: nil)
        ring.record(at: t(5), app: app("com.example.b"))
        let found = ring.appsIn(start: t(1), end: t(6))
        XCTAssertEqual(found.map(\.key), ["com.example.b"])
    }

    func testDuplicateAppInWindowIsReportedOnce() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(2), app: app("com.example.b"))
        ring.record(at: t(4), app: app("com.example.a"))
        let found = ring.appsIn(start: t(0), end: t(10))
        XCTAssertEqual(found.map(\.key), ["com.example.a", "com.example.b"])
    }

    /// A transition landing exactly on the window's end is included (the
    /// window is half-open at the start, closed at the end).
    func testTransitionExactlyAtWindowEndIsIncluded() {
        let ring = ForegroundRing()
        ring.record(at: t(0), app: app("com.example.a"))
        ring.record(at: t(10), app: app("com.example.b"))
        let found = ring.appsIn(start: t(8), end: t(10))
        XCTAssertEqual(found.map(\.key), ["com.example.a", "com.example.b"])
    }
}
