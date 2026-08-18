using System;
using System.Collections.Generic;

namespace ClipSync.Net;

/// Decides how a locally-built, fully-inline ClipboardItem goes on the
/// wire to one peer: which formats are inlined, which are streamed as
/// FileChunk/FileEnd, and which are dropped. Pure, so it can be tested
/// without a socket; PeerConnection applies the plan.
///
/// The original item is never mutated -- Broadcast hands one object to
/// every connection, and each connection may plan differently (a peer
/// without the stream capability gets fewer formats).
public static class StreamPlanner
{
    /// PROTOCOL.md §6.2: a format is inline iff its size is at most this.
    public const int MaxInlineBytes = 64 * 1024;

    /// PROTOCOL.md §10: total item size the sender will put on the wire.
    /// Formats past this are dropped in item order. Everything is held in
    /// memory end to end (item, frames, receive buffers, clipboard), and
    /// the receiver briefly holds ~2x this, so do not raise casually.
    public const ulong MaxItemBytes = 100UL * 1024 * 1024;

    /// PROTOCOL.md §6.5: FileChunk data at most this per frame.
    public const int ChunkBytes = 1024 * 1024;

    public sealed record OutStream(ulong StreamId, byte[] Data);
    public sealed record DroppedFormat(string Mime, ulong Size, string Reason);

    /// WireItem is null when nothing survived and the item should not be
    /// sent at all. Streams are in item order and must be sent, each as
    /// contiguous FileChunks then a FileEnd, after the wire item.
    public sealed record Result(ClipboardItem? WireItem, List<OutStream> Streams, List<DroppedFormat> Dropped);

    public static Result Plan(ClipboardItem item, bool peerStreams, Func<ulong> nextStreamId)
    {
        var streams = new List<OutStream>();
        var dropped = new List<DroppedFormat>();
        var kept = new List<ClipFormat>(item.Formats.Count);
        ulong total = 0;
        var changed = false;

        foreach (var f in item.Formats)
        {
            var size = f.Inline is { } d ? (ulong)d.Length : f.Size;
            if (total + size > MaxItemBytes)
            {
                dropped.Add(new DroppedFormat(f.Mime, size,
                    $"item would exceed {MaxItemBytes / (1024 * 1024)} MiB"));
                changed = true;
                continue;
            }
            if (size > (ulong)MaxInlineBytes)
            {
                if (!peerStreams)
                {
                    dropped.Add(new DroppedFormat(f.Mime, size, "peer lacks stream capability"));
                    changed = true;
                    continue;
                }
                if (f.Inline is null)
                {
                    // Already a stream reference (should not happen for a
                    // locally built item); nothing to send for it.
                    dropped.Add(new DroppedFormat(f.Mime, size, "no payload"));
                    changed = true;
                    continue;
                }
                var id = nextStreamId();
                streams.Add(new OutStream(id, f.Inline));
                kept.Add(new ClipFormat(f.Mime, size, null, id));
                changed = true;
            }
            else
            {
                kept.Add(f);
            }
            total += size;
        }

        if (kept.Count == 0) return new Result(null, streams, dropped);
        var wire = changed ? item with { Formats = kept } : item;
        return new Result(wire, streams, dropped);
    }
}
