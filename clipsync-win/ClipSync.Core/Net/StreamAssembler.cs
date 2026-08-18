using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ClipSync.Net;

public enum StreamOutcome { Ok, Ignored, Dropped }
public readonly record struct StreamResult(StreamOutcome Outcome, string? Reason = null)
{
    public static readonly StreamResult Ok = new(StreamOutcome.Ok);
    public static StreamResult Ignore(string why) => new(StreamOutcome.Ignored, why);
    public static StreamResult Drop(string why) => new(StreamOutcome.Dropped, why);
}

/// Reassembles one connection's streamed ClipboardItem: the wire item is
/// parked, FileChunks fill a buffer per stream_id, FileEnd verifies, and
/// once every stream is done the item is rebuilt with those payloads
/// inline -- indistinguishable downstream from an item that arrived
/// inline (writers, loop-suppression hash, history). Pure state machine;
/// PeerConnection feeds it and logs the reasons it returns.
///
/// One pending item at a time: the sender is sequential, so a new parked
/// item means any unfinished older one is stale and is discarded.
public sealed class StreamAssembler
{
    private sealed class Slot
    {
        public required ClipFormat Format;
        public required byte[] Buffer;
        public int Received;
        public bool Done;
    }

    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(30);

    private ClipboardItem? _pending;
    private readonly Dictionary<ulong, Slot> _slots = new();
    private DateTime _lastProgress;

    public bool HasPending => _pending is not null;

    /// True if the item carries any stream_id format and so must be parked.
    public static bool NeedsAssembly(ClipboardItem item)
    {
        foreach (var f in item.Formats)
            if (f.Inline is null && f.StreamId is not null) return true;
        return false;
    }

    /// Park a wire item whose formats include stream_ids. Rejects (and
    /// parks nothing) if the declared sizes exceed the cap, so a peer can
    /// never make us allocate more than we would send ourselves.
    public StreamResult Park(ClipboardItem wire, DateTime now)
    {
        ulong total = 0;
        foreach (var f in wire.Formats)
        {
            var size = f.Inline is { } d ? (ulong)d.Length : f.Size;
            if (size > StreamPlanner.MaxItemBytes)
                return StreamResult.Drop($"declared {f.Mime} size {size} exceeds cap");
            total += size;
            if (total > StreamPlanner.MaxItemBytes)
                return StreamResult.Drop($"declared item size exceeds cap");
        }

        var replaced = _pending is not null;
        Reset();
        _pending = wire;
        foreach (var f in wire.Formats)
        {
            if (f.Inline is not null || f.StreamId is not { } id) continue;
            if (_slots.ContainsKey(id)) { Reset(); return StreamResult.Drop($"duplicate stream_id {id}"); }
            _slots[id] = new Slot { Format = f, Buffer = new byte[f.Size] };
        }
        _lastProgress = now;
        return replaced ? new StreamResult(StreamOutcome.Ok, "replaced an incomplete item") : StreamResult.Ok;
    }

    public StreamResult Chunk(ulong streamId, ulong offset, ReadOnlySpan<byte> data, DateTime now)
    {
        if (!_slots.TryGetValue(streamId, out var s)) return StreamResult.Ignore($"unknown stream_id {streamId}");
        if (s.Done) { Reset(); return StreamResult.Drop($"chunk after end for stream {streamId}"); }
        if (offset != (ulong)s.Received) { Reset(); return StreamResult.Drop($"offset {offset} != received {s.Received} for stream {streamId}"); }
        if ((ulong)s.Received + (ulong)data.Length > (ulong)s.Buffer.Length) { Reset(); return StreamResult.Drop($"chunk overruns declared size for stream {streamId}"); }
        data.CopyTo(s.Buffer.AsSpan(s.Received));
        s.Received += data.Length;
        _lastProgress = now;
        return StreamResult.Ok;
    }

    public StreamResult End(ulong streamId, ulong totalSize, ReadOnlySpan<byte> sha256, DateTime now)
    {
        if (!_slots.TryGetValue(streamId, out var s)) return StreamResult.Ignore($"unknown stream_id {streamId}");
        if (s.Done) { Reset(); return StreamResult.Drop($"duplicate end for stream {streamId}"); }
        if (totalSize != (ulong)s.Buffer.Length || s.Received != s.Buffer.Length)
        { Reset(); return StreamResult.Drop($"end size {totalSize}/{s.Received} != declared {s.Buffer.Length} for stream {streamId}"); }
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(s.Buffer, actual);
        if (!actual.SequenceEqual(sha256)) { Reset(); return StreamResult.Drop($"hash mismatch for stream {streamId}"); }
        s.Done = true;
        _lastProgress = now;
        return StreamResult.Ok;
    }

    /// The materialized item once every stream has verified, else null.
    /// Consumes the pending state.
    public ClipboardItem? TakeCompleted()
    {
        if (_pending is null) return null;
        foreach (var s in _slots.Values) if (!s.Done) return null;
        var formats = new List<ClipFormat>(_pending.Formats.Count);
        foreach (var f in _pending.Formats)
        {
            if (f.Inline is null && f.StreamId is { } id && _slots.TryGetValue(id, out var s))
                formats.Add(new ClipFormat(f.Mime, (ulong)s.Buffer.Length, s.Buffer, null));
            else
                formats.Add(f);
        }
        var item = _pending with { Formats = formats };
        Reset();
        return item;
    }

    public bool IsStale(DateTime now) => _pending is not null && now - _lastProgress > IdleWindow;

    /// Drop the pending item if it has seen no progress within the window.
    /// Returns true if something was dropped.
    public bool DropStale(DateTime now)
    {
        if (!IsStale(now)) return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        _pending = null;
        _slots.Clear();
    }
}
