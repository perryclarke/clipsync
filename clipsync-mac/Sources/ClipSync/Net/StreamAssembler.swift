import Foundation
import Crypto

enum StreamOutcome: Equatable { case ok, ignored, dropped }
struct StreamResult: Equatable {
    let outcome: StreamOutcome
    let reason: String?
    static let ok = StreamResult(outcome: .ok, reason: nil)
    static func ignore(_ why: String) -> StreamResult { StreamResult(outcome: .ignored, reason: why) }
    static func drop(_ why: String) -> StreamResult { StreamResult(outcome: .dropped, reason: why) }
}

/// Reassembles one connection's streamed ClipboardItem: the wire item is
/// parked, FileChunks fill a buffer per stream_id, FileEnd verifies, and
/// once every stream is done the item is rebuilt with those payloads
/// inline -- indistinguishable downstream from an item that arrived
/// inline (writer, loop-suppression hash, Clipboard History). Pure state
/// machine; PeerConnection feeds it and logs the reasons it returns.
///
/// One pending item at a time: the sender is sequential, so a new parked
/// item means any unfinished older one is stale and is discarded.
///
/// Mirrors the Windows StreamAssembler.
final class StreamAssembler {
    private final class Slot {
        let format: ClipFormat
        var buffer: Data
        var received = 0
        var done = false
        init(format: ClipFormat) {
            self.format = format
            self.buffer = Data(count: Int(format.size))
        }
    }

    static let idleWindow: TimeInterval = 30

    private var pending: ClipboardItem?
    private var slots: [UInt64: Slot] = [:]
    private var lastProgress = Date.distantPast

    var hasPending: Bool { pending != nil }

    /// True if the item carries any stream_id format and so must be parked.
    static func needsAssembly(_ item: ClipboardItem) -> Bool {
        item.formats.contains { if case .stream = $0.payload { return true } else { return false } }
    }

    /// Park a wire item whose formats include stream_ids. Rejects (and
    /// parks nothing) if the declared sizes exceed the cap, so a peer can
    /// never make us allocate more than we would send ourselves.
    func park(_ wire: ClipboardItem, now: Date) -> StreamResult {
        var total: UInt64 = 0
        for f in wire.formats {
            let size: UInt64
            if case .inline(let d) = f.payload { size = UInt64(d.count) } else { size = f.size }
            if size > StreamPlanner.maxItemBytes {
                return .drop("declared \(f.mime) size \(size) exceeds cap")
            }
            total += size
            if total > StreamPlanner.maxItemBytes { return .drop("declared item size exceeds cap") }
        }

        let replaced = pending != nil
        reset()
        pending = wire
        for f in wire.formats {
            guard case .stream(let id) = f.payload else { continue }
            if slots[id] != nil { reset(); return .drop("duplicate stream_id \(id)") }
            slots[id] = Slot(format: f)
        }
        lastProgress = now
        return replaced ? StreamResult(outcome: .ok, reason: "replaced an incomplete item") : .ok
    }

    func chunk(streamId: UInt64, offset: UInt64, data: Data, now: Date) -> StreamResult {
        guard let s = slots[streamId] else { return .ignore("unknown stream_id \(streamId)") }
        if s.done { reset(); return .drop("chunk after end for stream \(streamId)") }
        if offset != UInt64(s.received) {
            reset(); return .drop("offset \(offset) != received \(s.received) for stream \(streamId)")
        }
        if s.received + data.count > s.buffer.count {
            reset(); return .drop("chunk overruns declared size for stream \(streamId)")
        }
        s.buffer.replaceSubrange(s.received ..< s.received + data.count, with: data)
        s.received += data.count
        lastProgress = now
        return .ok
    }

    func end(streamId: UInt64, totalSize: UInt64, sha256: Data, now: Date) -> StreamResult {
        guard let s = slots[streamId] else { return .ignore("unknown stream_id \(streamId)") }
        if s.done { reset(); return .drop("duplicate end for stream \(streamId)") }
        if totalSize != UInt64(s.buffer.count) || s.received != s.buffer.count {
            reset()
            return .drop("end size \(totalSize)/\(s.received) != declared \(s.buffer.count) for stream \(streamId)")
        }
        let actual = Data(SHA256.hash(data: s.buffer))
        if actual != sha256 { reset(); return .drop("hash mismatch for stream \(streamId)") }
        s.done = true
        lastProgress = now
        return .ok
    }

    /// The materialised item once every stream has verified, else nil.
    /// Consumes the pending state.
    func takeCompleted() -> ClipboardItem? {
        guard var item = pending else { return nil }
        if slots.values.contains(where: { !$0.done }) { return nil }
        item.formats = item.formats.map { f in
            if case .stream(let id) = f.payload, let s = slots[id] {
                return ClipFormat(mime: f.mime, size: UInt64(s.buffer.count), payload: .inline(s.buffer))
            }
            return f
        }
        reset()
        return item
    }

    func isStale(now: Date) -> Bool {
        pending != nil && now.timeIntervalSince(lastProgress) > Self.idleWindow
    }

    /// Drop the pending item if it has seen no progress within the window.
    /// Returns true if something was dropped.
    @discardableResult
    func dropStale(now: Date) -> Bool {
        guard isStale(now: now) else { return false }
        reset()
        return true
    }

    func reset() {
        pending = nil
        slots.removeAll()
    }
}
