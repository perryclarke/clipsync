using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ClipSync.Net;
using Xunit;

namespace ClipSync.Tests;

public class StreamAssemblerTests
{
    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;
    private static readonly DateTime T0 = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Bytes(int n, byte seed = 1)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(seed + i);
        return b;
    }

    /// An original (all-inline) item and the wire form the planner would send.
    private static (ClipboardItem original, ClipboardItem wire, List<StreamPlanner.OutStream> streams)
        Split(params (string mime, byte[] data)[] fmts)
    {
        var formats = fmts.Select(f => new ClipFormat(f.mime, (ulong)f.data.Length, f.data, null)).ToList();
        var original = new ClipboardItem(5, Bytes(32), 123, formats, "h");
        var id = _nextId;                       // per-"connection" counter: never repeats
        var plan = StreamPlanner.Plan(original, peerStreams: true, () => id++);
        _nextId = id;
        return (original, plan.WireItem!, plan.Streams);
    }
    private static ulong _nextId = 10;

    private static byte[] Sha(byte[] d) => SHA256.HashData(d);

    /// Drive a whole stream through, in 1 MiB slices.
    private static void Feed(StreamAssembler a, StreamPlanner.OutStream s, DateTime t)
    {
        ulong off = 0;
        foreach (var slice in s.Data.Chunk(MiB))
        {
            var r = a.Chunk(s.StreamId, off, slice, t);
            Assert.Equal(StreamOutcome.Ok, r.Outcome);
            off += (ulong)slice.Length;
        }
        var e = a.End(s.StreamId, (ulong)s.Data.Length, Sha(s.Data), t);
        Assert.Equal(StreamOutcome.Ok, e.Outcome);
    }

    [Fact]
    public void Happy_path_materializes_an_inline_item_with_the_original_hash()
    {
        var (original, wire, streams) = Split(("text/plain", Bytes(20)), ("image/png", Bytes(3 * MiB + 17)));
        var a = new StreamAssembler();

        Assert.Equal(StreamOutcome.Ok, a.Park(wire, T0).Outcome);
        Assert.Null(a.TakeCompleted());
        Feed(a, streams[0], T0);

        var done = a.TakeCompleted();
        Assert.NotNull(done);
        Assert.All(done!.Formats, f => Assert.NotNull(f.Inline));
        Assert.Equal(original.Formats[1].Inline, done.Formats[1].Inline);
        Assert.Equal(original.CanonicalHash(), done.CanonicalHash());
        Assert.Equal(original.Seq, done.Seq);
        Assert.Null(a.TakeCompleted());              // consumed
        Assert.False(a.HasPending);
    }

    [Fact]
    public void Two_streams_complete_in_any_order()
    {
        var (original, wire, streams) = Split(("image/png", Bytes(MiB, 1)), ("image/tiff", Bytes(2 * MiB, 9)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        Feed(a, streams[1], T0);
        Assert.Null(a.TakeCompleted());
        Feed(a, streams[0], T0);
        var done = a.TakeCompleted()!;
        Assert.Equal(original.CanonicalHash(), done.CanonicalHash());
    }

    [Fact]
    public void Chunk_for_unknown_stream_is_ignored_not_fatal()
    {
        var (_, wire, streams) = Split(("image/png", Bytes(MiB)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        var r = a.Chunk(9999, 0, Bytes(10), T0);
        Assert.Equal(StreamOutcome.Ignored, r.Outcome);
        Assert.True(a.HasPending);
        Feed(a, streams[0], T0);
        Assert.NotNull(a.TakeCompleted());
    }

    [Fact]
    public void Out_of_order_offset_drops_the_pending_item()
    {
        var (_, wire, streams) = Split(("image/png", Bytes(2 * MiB)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        Assert.Equal(StreamOutcome.Ok, a.Chunk(streams[0].StreamId, 0, Bytes(MiB), T0).Outcome);
        var r = a.Chunk(streams[0].StreamId, 0, Bytes(MiB), T0);   // repeats offset 0
        Assert.Equal(StreamOutcome.Dropped, r.Outcome);
        Assert.Contains("offset", r.Reason);
        Assert.False(a.HasPending);
    }

    [Fact]
    public void Chunk_past_declared_size_drops_the_pending_item()
    {
        var (_, wire, streams) = Split(("image/png", Bytes(MiB)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        var r = a.Chunk(streams[0].StreamId, 0, Bytes(MiB + 1), T0);
        Assert.Equal(StreamOutcome.Dropped, r.Outcome);
        Assert.False(a.HasPending);
    }

    [Fact]
    public void Hash_mismatch_drops_the_pending_item()
    {
        var (_, wire, streams) = Split(("image/png", Bytes(MiB)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        a.Chunk(streams[0].StreamId, 0, streams[0].Data, T0);
        var r = a.End(streams[0].StreamId, MiB, new byte[32], T0);
        Assert.Equal(StreamOutcome.Dropped, r.Outcome);
        Assert.Contains("hash", r.Reason);
        Assert.False(a.HasPending);
    }

    [Fact]
    public void End_before_all_bytes_drops_the_pending_item()
    {
        var (_, wire, streams) = Split(("image/png", Bytes(2 * MiB)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        a.Chunk(streams[0].StreamId, 0, Bytes(MiB), T0);
        var r = a.End(streams[0].StreamId, 2 * MiB, Sha(streams[0].Data), T0);
        Assert.Equal(StreamOutcome.Dropped, r.Outcome);
    }

    [Fact]
    public void Declared_size_over_cap_is_rejected_at_park()
    {
        var wire = new ClipboardItem(1, Bytes(32), 0, new List<ClipFormat>
        {
            new("image/png", 100UL * MiB + 1, null, 7),
        }, null);
        var a = new StreamAssembler();
        var r = a.Park(wire, T0);
        Assert.Equal(StreamOutcome.Dropped, r.Outcome);
        Assert.False(a.HasPending);
    }

    [Fact]
    public void Declared_sizes_summing_over_cap_are_rejected_at_park()
    {
        var wire = new ClipboardItem(1, Bytes(32), 0, new List<ClipFormat>
        {
            new("image/png", 60UL * MiB, null, 7),
            new("image/tiff", 41UL * MiB, null, 8),
        }, null);
        var a = new StreamAssembler();
        Assert.Equal(StreamOutcome.Dropped, a.Park(wire, T0).Outcome);
    }

    [Fact]
    public void All_inline_item_is_not_parked()
    {
        var wire = new ClipboardItem(1, Bytes(32), 0, new List<ClipFormat>
        {
            new("text/plain", 3, Bytes(3), null),
        }, null);
        var a = new StreamAssembler();
        Assert.False(StreamAssembler.NeedsAssembly(wire));
    }

    [Fact]
    public void New_parked_item_replaces_an_incomplete_one()
    {
        var (_, wire1, streams1) = Split(("image/png", Bytes(MiB, 1)));
        var (orig2, wire2, streams2) = Split(("image/png", Bytes(MiB, 2)));
        var a = new StreamAssembler();
        a.Park(wire1, T0);
        a.Chunk(streams1[0].StreamId, 0, Bytes(KiB), T0);
        var r = a.Park(wire2, T0);
        Assert.Equal(StreamOutcome.Ok, r.Outcome);
        Assert.Contains("replac", r.Reason);          // says it replaced one
        // Old stream id is now unknown, new one completes.
        Assert.Equal(StreamOutcome.Ignored, a.Chunk(streams1[0].StreamId, KiB, Bytes(KiB), T0).Outcome);
        Feed(a, streams2[0], T0);
        Assert.Equal(orig2.CanonicalHash(), a.TakeCompleted()!.CanonicalHash());
    }

    [Fact]
    public void Idle_pending_item_is_stale_after_the_window()
    {
        var (_, wire, streams) = Split(("image/png", Bytes(2 * MiB)));
        var a = new StreamAssembler();
        a.Park(wire, T0);
        a.Chunk(streams[0].StreamId, 0, Bytes(MiB), T0.AddSeconds(5));
        Assert.False(a.IsStale(T0.AddSeconds(30)));
        Assert.True(a.IsStale(T0.AddSeconds(36)));
        a.DropStale(T0.AddSeconds(36));
        Assert.False(a.HasPending);
    }
}
