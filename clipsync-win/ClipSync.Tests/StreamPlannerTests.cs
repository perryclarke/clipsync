using System;
using System.Collections.Generic;
using System.Linq;
using ClipSync.Net;
using Xunit;

namespace ClipSync.Tests;

public class StreamPlannerTests
{
    private static ClipFormat Fmt(string mime, int size) =>
        new(mime, (ulong)size, new byte[size], null);

    private static ClipboardItem Item(params ClipFormat[] formats) =>
        new(1, new byte[32], 0, formats.ToList(), null);

    private static ulong _next = 100;
    private static ulong NextId() => _next++;

    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;

    [Fact]
    public void Small_item_passes_through_inline_with_no_streams()
    {
        var item = Item(Fmt("text/plain", 10), Fmt("text/html", 64 * KiB));
        var plan = StreamPlanner.Plan(item, peerStreams: true, NextId);

        Assert.NotNull(plan.WireItem);
        Assert.Same(item, plan.WireItem);          // untouched object
        Assert.Empty(plan.Streams);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void Large_format_is_replaced_by_stream_id_and_queued()
    {
        var text = Fmt("text/plain", 10);
        var img = Fmt("image/png", 64 * KiB + 1);
        var plan = StreamPlanner.Plan(Item(text, img), peerStreams: true, NextId);

        var wire = plan.WireItem!;
        Assert.Equal(2, wire.Formats.Count);
        Assert.NotNull(wire.Formats[0].Inline);
        Assert.Null(wire.Formats[0].StreamId);
        Assert.Null(wire.Formats[1].Inline);
        Assert.NotNull(wire.Formats[1].StreamId);
        Assert.Equal(img.Size, wire.Formats[1].Size);

        var s = Assert.Single(plan.Streams);
        Assert.Equal(wire.Formats[1].StreamId, s.StreamId);
        Assert.Same(img.Inline, s.Data);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void Distinct_streams_get_distinct_ids()
    {
        var plan = StreamPlanner.Plan(
            Item(Fmt("image/png", MiB), Fmt("image/tiff", MiB)), peerStreams: true, NextId);
        Assert.Equal(2, plan.Streams.Count);
        Assert.NotEqual(plan.Streams[0].StreamId, plan.Streams[1].StreamId);
    }

    [Fact]
    public void Cap_drops_later_formats_in_order_and_keeps_earlier_ones()
    {
        // 60 + 50 > 100 MiB: the second is dropped, the third (small) still fits.
        var a = Fmt("image/png", 60 * MiB);
        var b = Fmt("image/tiff", 50 * MiB);
        var c = Fmt("text/plain", 100);
        var plan = StreamPlanner.Plan(Item(a, b, c), peerStreams: true, NextId);

        Assert.Equal(new[] { "image/png", "text/plain" }, plan.WireItem!.Formats.Select(f => f.Mime));
        var d = Assert.Single(plan.Dropped);
        Assert.Equal("image/tiff", d.Mime);
        Assert.Contains("100 MiB", d.Reason);
    }

    [Fact]
    public void Item_at_exactly_the_cap_is_kept()
    {
        var plan = StreamPlanner.Plan(Item(Fmt("image/png", 100 * MiB)), peerStreams: true, NextId);
        Assert.NotNull(plan.WireItem);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void Nothing_fits_yields_no_wire_item()
    {
        var plan = StreamPlanner.Plan(Item(Fmt("image/png", 100 * MiB + 1)), peerStreams: true, NextId);
        Assert.Null(plan.WireItem);
        Assert.Empty(plan.Streams);
        Assert.Single(plan.Dropped);
    }

    [Fact]
    public void Peer_without_stream_cap_gets_only_inline_sized_formats()
    {
        var text = Fmt("text/plain", 10);
        var img = Fmt("image/png", MiB);
        var plan = StreamPlanner.Plan(Item(text, img), peerStreams: false, NextId);

        Assert.Equal(new[] { "text/plain" }, plan.WireItem!.Formats.Select(f => f.Mime));
        Assert.Empty(plan.Streams);
        var d = Assert.Single(plan.Dropped);
        Assert.Contains("stream", d.Reason);
    }

    [Fact]
    public void Peer_without_stream_cap_and_only_large_formats_yields_nothing()
    {
        var plan = StreamPlanner.Plan(Item(Fmt("image/png", MiB)), peerStreams: false, NextId);
        Assert.Null(plan.WireItem);
    }

    [Fact]
    public void Wire_item_keeps_seq_origin_ts_and_hint()
    {
        var item = new ClipboardItem(42, Enumerable.Repeat((byte)7, 32).ToArray(), 999,
            new List<ClipFormat> { Fmt("image/png", MiB) }, "hint");
        var wire = StreamPlanner.Plan(item, peerStreams: true, NextId).WireItem!;
        Assert.Equal(42UL, wire.Seq);
        Assert.Equal(item.OriginDid, wire.OriginDid);
        Assert.Equal(999UL, wire.TsMs);
        Assert.Equal("hint", wire.Hint);
    }
}
