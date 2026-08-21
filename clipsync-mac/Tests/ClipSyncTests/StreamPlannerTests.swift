import XCTest
@testable import ClipSync

final class StreamPlannerTests: XCTestCase {
    private let kib = 1024
    private let mib = 1024 * 1024
    private var next: UInt64 = 100
    private func nextId() -> UInt64 { defer { next += 1 }; return next }

    private func fmt(_ mime: String, _ size: Int) -> ClipFormat {
        ClipFormat(mime: mime, size: UInt64(size), payload: .inline(Data(count: size)))
    }
    private func item(_ formats: ClipFormat...) -> ClipboardItem {
        ClipboardItem(seq: 1, originDid: Data(count: 32), tsMs: 0, formats: formats, hint: nil)
    }
    private func isInline(_ f: ClipFormat) -> Bool { if case .inline = f.payload { return true } else { return false } }
    private func streamId(_ f: ClipFormat) -> UInt64? { if case .stream(let id) = f.payload { return id } else { return nil } }

    func testSmallItemPassesThroughInlineWithNoStreams() {
        let plan = StreamPlanner.plan(item(fmt("text/plain", 10), fmt("text/html", 64 * kib)),
                                      peerStreams: true, nextStreamId: nextId)
        XCTAssertNotNil(plan.wireItem)
        XCTAssertTrue(plan.wireItem!.formats.allSatisfy(isInline))
        XCTAssertTrue(plan.streams.isEmpty)
        XCTAssertTrue(plan.dropped.isEmpty)
    }

    func testLargeFormatIsReplacedByStreamIdAndQueued() {
        let big = fmt("image/png", 64 * kib + 1)
        let plan = StreamPlanner.plan(item(fmt("text/plain", 10), big), peerStreams: true, nextStreamId: nextId)
        let wire = plan.wireItem!
        XCTAssertEqual(wire.formats.count, 2)
        XCTAssertTrue(isInline(wire.formats[0]))
        XCTAssertNotNil(streamId(wire.formats[1]))
        XCTAssertEqual(wire.formats[1].size, big.size)
        XCTAssertEqual(plan.streams.count, 1)
        XCTAssertEqual(plan.streams[0].streamId, streamId(wire.formats[1]))
        XCTAssertEqual(plan.streams[0].data.count, 64 * kib + 1)
        XCTAssertTrue(plan.dropped.isEmpty)
    }

    func testDistinctStreamsGetDistinctIds() {
        let plan = StreamPlanner.plan(item(fmt("image/png", mib), fmt("image/tiff", mib)),
                                      peerStreams: true, nextStreamId: nextId)
        XCTAssertEqual(plan.streams.count, 2)
        XCTAssertNotEqual(plan.streams[0].streamId, plan.streams[1].streamId)
    }

    func testCapDropsLaterFormatsInOrderAndKeepsEarlierOnes() {
        let plan = StreamPlanner.plan(
            item(fmt("image/png", 60 * mib), fmt("image/tiff", 50 * mib), fmt("text/plain", 100)),
            peerStreams: true, nextStreamId: nextId)
        XCTAssertEqual(plan.wireItem!.formats.map(\.mime), ["image/png", "text/plain"])
        XCTAssertEqual(plan.dropped.count, 1)
        XCTAssertEqual(plan.dropped[0].mime, "image/tiff")
        XCTAssertTrue(plan.dropped[0].reason.contains("100 MiB"))
    }

    func testItemAtExactlyTheCapIsKept() {
        let plan = StreamPlanner.plan(item(fmt("image/png", 100 * mib)), peerStreams: true, nextStreamId: nextId)
        XCTAssertNotNil(plan.wireItem)
        XCTAssertTrue(plan.dropped.isEmpty)
    }

    func testNothingFitsYieldsNoWireItem() {
        let plan = StreamPlanner.plan(item(fmt("image/png", 100 * mib + 1)), peerStreams: true, nextStreamId: nextId)
        XCTAssertNil(plan.wireItem)
        XCTAssertTrue(plan.streams.isEmpty)
        XCTAssertEqual(plan.dropped.count, 1)
    }

    func testPeerWithoutStreamCapGetsOnlyInlineSizedFormats() {
        let plan = StreamPlanner.plan(item(fmt("text/plain", 10), fmt("image/png", mib)),
                                      peerStreams: false, nextStreamId: nextId)
        XCTAssertEqual(plan.wireItem!.formats.map(\.mime), ["text/plain"])
        XCTAssertTrue(plan.streams.isEmpty)
        XCTAssertEqual(plan.dropped.count, 1)
        XCTAssertTrue(plan.dropped[0].reason.contains("stream"))
    }

    func testPeerWithoutStreamCapAndOnlyLargeFormatsYieldsNothing() {
        let plan = StreamPlanner.plan(item(fmt("image/png", mib)), peerStreams: false, nextStreamId: nextId)
        XCTAssertNil(plan.wireItem)
    }

    func testWireItemKeepsSeqOriginTsAndHint() {
        let did = Data(repeating: 7, count: 32)
        let src = ClipboardItem(seq: 42, originDid: did, tsMs: 999, formats: [fmt("image/png", mib)], hint: "hint")
        let wire = StreamPlanner.plan(src, peerStreams: true, nextStreamId: nextId).wireItem!
        XCTAssertEqual(wire.seq, 42)
        XCTAssertEqual(wire.originDid, did)
        XCTAssertEqual(wire.tsMs, 999)
        XCTAssertEqual(wire.hint, "hint")
    }
}
