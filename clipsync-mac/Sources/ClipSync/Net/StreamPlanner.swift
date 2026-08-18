import Foundation

/// Decides how a locally-built, fully-inline ClipboardItem goes on the
/// wire to one peer: which formats are inlined, which are streamed as
/// FileChunk/FileEnd, and which are dropped. Pure, so it can be tested
/// without a socket; PeerConnection applies the plan.
///
/// The original item is never mutated -- broadcast hands one value to
/// every connection, and each connection may plan differently (a peer
/// without the stream capability gets fewer formats).
///
/// Mirrors the Windows StreamPlanner; keep the limits identical.
enum StreamPlanner {
    /// PROTOCOL.md §6.2: a format is inline iff its size is at most this.
    static let maxInlineBytes = 64 * 1024

    /// PROTOCOL.md §10: total item size the sender will put on the wire.
    /// Formats past this are dropped in item order. Everything is held in
    /// memory end to end (item, frames, receive buffers, pasteboard), and
    /// the receiver briefly holds ~2x this, so do not raise casually.
    static let maxItemBytes: UInt64 = 100 * 1024 * 1024

    /// PROTOCOL.md §6.5: FileChunk data at most this per frame.
    static let chunkBytes = 1024 * 1024

    struct OutStream: Equatable {
        let streamId: UInt64
        let data: Data
    }
    struct Dropped: Equatable {
        let mime: String
        let size: UInt64
        let reason: String
    }
    /// `wireItem` is nil when nothing survived and the item should not be
    /// sent at all. `streams` are in item order and must be sent, each as
    /// contiguous FileChunks then a FileEnd, after the wire item.
    struct Result {
        let wireItem: ClipboardItem?
        let streams: [OutStream]
        let dropped: [Dropped]
    }

    static func plan(_ item: ClipboardItem, peerStreams: Bool,
                     nextStreamId: () -> UInt64) -> Result {
        var streams: [OutStream] = []
        var dropped: [Dropped] = []
        var kept: [ClipFormat] = []
        var total: UInt64 = 0

        for f in item.formats {
            let size: UInt64
            if case .inline(let d) = f.payload { size = UInt64(d.count) } else { size = f.size }

            if total + size > maxItemBytes {
                dropped.append(Dropped(mime: f.mime, size: size,
                                       reason: "item would exceed \(maxItemBytes / (1024 * 1024)) MiB"))
                continue
            }
            if size > UInt64(maxInlineBytes) {
                guard peerStreams else {
                    dropped.append(Dropped(mime: f.mime, size: size, reason: "peer lacks stream capability"))
                    continue
                }
                guard case .inline(let d) = f.payload else {
                    dropped.append(Dropped(mime: f.mime, size: size, reason: "no payload"))
                    continue
                }
                let id = nextStreamId()
                streams.append(OutStream(streamId: id, data: d))
                kept.append(ClipFormat(mime: f.mime, size: size, payload: .stream(id)))
            } else {
                kept.append(f)
            }
            total += size
        }

        guard !kept.isEmpty else { return Result(wireItem: nil, streams: streams, dropped: dropped) }
        var wire = item
        wire.formats = kept
        return Result(wireItem: wire, streams: streams, dropped: dropped)
    }
}
