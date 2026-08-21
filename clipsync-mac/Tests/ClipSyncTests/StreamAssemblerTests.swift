import XCTest
import Crypto
@testable import ClipSync

final class StreamAssemblerTests: XCTestCase {
    private let kib = 1024
    private let mib = 1024 * 1024
    private let t0 = Date(timeIntervalSince1970: 1_800_000_000)
    private var nextId: UInt64 = 10

    private func bytes(_ n: Int, seed: UInt8 = 1) -> Data {
        var d = Data(count: n)
        for i in 0 ..< n { d[i] = seed &+ UInt8(truncatingIfNeeded: i) }
        return d
    }
    private func sha(_ d: Data) -> Data { Data(SHA256.hash(data: d)) }

    /// An original (all-inline) item and the wire form the planner would send.
    private func split(_ fmts: (String, Data)...) -> (ClipboardItem, ClipboardItem, [StreamPlanner.OutStream]) {
        let formats = fmts.map { ClipFormat(mime: $0.0, size: UInt64($0.1.count), payload: .inline($0.1)) }
        let original = ClipboardItem(seq: 5, originDid: bytes(32), tsMs: 123, formats: formats, hint: "h")
        let plan = StreamPlanner.plan(original, peerStreams: true) { defer { nextId += 1 }; return nextId }
        return (original, plan.wireItem!, plan.streams)
    }

    /// Drive a whole stream through, in 1 MiB slices.
    private func feed(_ a: StreamAssembler, _ s: StreamPlanner.OutStream, _ t: Date,
                      file: StaticString = #filePath, line: UInt = #line) {
        var off = 0
        while off < s.data.count {
            let n = min(mib, s.data.count - off)
            let r = a.chunk(streamId: s.streamId, offset: UInt64(off), data: s.data.subdata(in: off ..< off + n), now: t)
            XCTAssertEqual(r.outcome, .ok, r.reason ?? "", file: file, line: line)
            off += n
        }
        let e = a.end(streamId: s.streamId, totalSize: UInt64(s.data.count), sha256: sha(s.data), now: t)
        XCTAssertEqual(e.outcome, .ok, e.reason ?? "", file: file, line: line)
    }
    private func inline(_ f: ClipFormat) -> Data? { if case .inline(let d) = f.payload { return d } else { return nil } }

    func testHappyPathMaterializesAnInlineItemWithTheOriginalHash() {
        let (original, wire, streams) = split(("text/plain", bytes(20)), ("image/png", bytes(3 * mib + 17)))
        let a = StreamAssembler()
        XCTAssertEqual(a.park(wire, now: t0).outcome, .ok)
        XCTAssertNil(a.takeCompleted())
        feed(a, streams[0], t0)
        let done = a.takeCompleted()
        XCTAssertNotNil(done)
        XCTAssertTrue(done!.formats.allSatisfy { inline($0) != nil })
        XCTAssertEqual(inline(done!.formats[1]), inline(original.formats[1]))
        XCTAssertEqual(done!.canonicalHash, original.canonicalHash)
        XCTAssertEqual(done!.seq, original.seq)
        XCTAssertNil(a.takeCompleted())
        XCTAssertFalse(a.hasPending)
    }

    func testTwoStreamsCompleteInAnyOrder() {
        let (original, wire, streams) = split(("image/png", bytes(mib, seed: 1)), ("image/tiff", bytes(2 * mib, seed: 9)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        feed(a, streams[1], t0)
        XCTAssertNil(a.takeCompleted())
        feed(a, streams[0], t0)
        XCTAssertEqual(a.takeCompleted()!.canonicalHash, original.canonicalHash)
    }

    func testChunkForUnknownStreamIsIgnoredNotFatal() {
        let (_, wire, streams) = split(("image/png", bytes(mib)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        XCTAssertEqual(a.chunk(streamId: 9999, offset: 0, data: bytes(10), now: t0).outcome, .ignored)
        XCTAssertTrue(a.hasPending)
        feed(a, streams[0], t0)
        XCTAssertNotNil(a.takeCompleted())
    }

    func testOutOfOrderOffsetDropsThePendingItem() {
        let (_, wire, streams) = split(("image/png", bytes(2 * mib)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        XCTAssertEqual(a.chunk(streamId: streams[0].streamId, offset: 0, data: bytes(mib), now: t0).outcome, .ok)
        let r = a.chunk(streamId: streams[0].streamId, offset: 0, data: bytes(mib), now: t0)
        XCTAssertEqual(r.outcome, .dropped)
        XCTAssertTrue(r.reason!.contains("offset"))
        XCTAssertFalse(a.hasPending)
    }

    func testChunkPastDeclaredSizeDropsThePendingItem() {
        let (_, wire, streams) = split(("image/png", bytes(mib)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        XCTAssertEqual(a.chunk(streamId: streams[0].streamId, offset: 0, data: bytes(mib + 1), now: t0).outcome, .dropped)
        XCTAssertFalse(a.hasPending)
    }

    func testHashMismatchDropsThePendingItem() {
        let (_, wire, streams) = split(("image/png", bytes(mib)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        _ = a.chunk(streamId: streams[0].streamId, offset: 0, data: streams[0].data, now: t0)
        let r = a.end(streamId: streams[0].streamId, totalSize: UInt64(mib), sha256: Data(count: 32), now: t0)
        XCTAssertEqual(r.outcome, .dropped)
        XCTAssertTrue(r.reason!.contains("hash"))
        XCTAssertFalse(a.hasPending)
    }

    func testEndBeforeAllBytesDropsThePendingItem() {
        let (_, wire, streams) = split(("image/png", bytes(2 * mib)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        _ = a.chunk(streamId: streams[0].streamId, offset: 0, data: bytes(mib), now: t0)
        let r = a.end(streamId: streams[0].streamId, totalSize: UInt64(2 * mib), sha256: sha(streams[0].data), now: t0)
        XCTAssertEqual(r.outcome, .dropped)
    }

    func testDeclaredSizeOverCapIsRejectedAtPark() {
        let wire = ClipboardItem(seq: 1, originDid: bytes(32), tsMs: 0, formats: [
            ClipFormat(mime: "image/png", size: 100 * 1024 * 1024 + 1, payload: .stream(7))
        ], hint: nil)
        let a = StreamAssembler()
        XCTAssertEqual(a.park(wire, now: t0).outcome, .dropped)
        XCTAssertFalse(a.hasPending)
    }

    func testDeclaredSizesSummingOverCapAreRejectedAtPark() {
        let wire = ClipboardItem(seq: 1, originDid: bytes(32), tsMs: 0, formats: [
            ClipFormat(mime: "image/png", size: 60 * 1024 * 1024, payload: .stream(7)),
            ClipFormat(mime: "image/tiff", size: 41 * 1024 * 1024, payload: .stream(8))
        ], hint: nil)
        XCTAssertEqual(StreamAssembler().park(wire, now: t0).outcome, .dropped)
    }

    func testAllInlineItemIsNotParked() {
        let wire = ClipboardItem(seq: 1, originDid: bytes(32), tsMs: 0, formats: [
            ClipFormat(mime: "text/plain", size: 3, payload: .inline(bytes(3)))
        ], hint: nil)
        XCTAssertFalse(StreamAssembler.needsAssembly(wire))
    }

    func testNewParkedItemReplacesAnIncompleteOne() {
        let (_, wire1, streams1) = split(("image/png", bytes(mib, seed: 1)))
        let (orig2, wire2, streams2) = split(("image/png", bytes(mib, seed: 2)))
        let a = StreamAssembler()
        _ = a.park(wire1, now: t0)
        _ = a.chunk(streamId: streams1[0].streamId, offset: 0, data: bytes(kib), now: t0)
        let r = a.park(wire2, now: t0)
        XCTAssertEqual(r.outcome, .ok)
        XCTAssertTrue(r.reason!.contains("replac"))
        XCTAssertEqual(a.chunk(streamId: streams1[0].streamId, offset: UInt64(kib), data: bytes(kib), now: t0).outcome, .ignored)
        feed(a, streams2[0], t0)
        XCTAssertEqual(a.takeCompleted()!.canonicalHash, orig2.canonicalHash)
    }

    func testIdlePendingItemIsStaleAfterTheWindow() {
        let (_, wire, streams) = split(("image/png", bytes(2 * mib)))
        let a = StreamAssembler()
        _ = a.park(wire, now: t0)
        _ = a.chunk(streamId: streams[0].streamId, offset: 0, data: bytes(mib), now: t0.addingTimeInterval(5))
        XCTAssertFalse(a.isStale(now: t0.addingTimeInterval(30)))
        XCTAssertTrue(a.isStale(now: t0.addingTimeInterval(36)))
        a.dropStale(now: t0.addingTimeInterval(36))
        XCTAssertFalse(a.hasPending)
    }
}
