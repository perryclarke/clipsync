using System;
using System.Collections.Generic;
using PeterO.Cbor;

namespace ClipSync.Net;

public enum MessageType : byte
{
    Hello = 1,
    ClipboardItem = 2,
    LargeItemOffer = 3,
    LargeItemAccept = 4,
    FileChunk = 5,
    FileEnd = 6,
    Ack = 7,
    Ping = 8,
    Pong = 9,
    ProtocolError = 10,
    EnrollSalt = 128,
    EnrollConfirmA = 129,
    EnrollConfirmB = 130,
    EnrollIdentity = 131,
}

public static class Codec
{
    public const int MaxFrameSize = 16 * 1024 * 1024;

    public static byte[] Frame(CBORObject body)
    {
        var bytes = body.EncodeToBytes(CBOREncodeOptions.Default);
        if (bytes.Length > MaxFrameSize) throw new InvalidOperationException("oversize");
        var len = (uint)bytes.Length;
        var outBuf = new byte[4 + bytes.Length];
        outBuf[0] = (byte)((len >> 24) & 0xff);
        outBuf[1] = (byte)((len >> 16) & 0xff);
        outBuf[2] = (byte)((len >> 8) & 0xff);
        outBuf[3] = (byte)(len & 0xff);
        Buffer.BlockCopy(bytes, 0, outBuf, 4, bytes.Length);
        return outBuf;
    }

    public static byte[] EncodeHello(byte[] did, string name, string[] caps)
    {
        var o = CBORObject.NewMap();
        o.Add("t", (int)MessageType.Hello);
        o.Add("v", 1);
        o.Add("did", did);
        o.Add("name", name);
        var arr = CBORObject.NewArray();
        foreach (var c in caps) arr.Add(c);
        o.Add("caps", arr);
        return Frame(o);
    }

    public static byte[] EncodePing()
    {
        var o = CBORObject.NewMap();
        o.Add("t", (int)MessageType.Ping);
        return Frame(o);
    }

    public static byte[] EncodePong()
    {
        var o = CBORObject.NewMap();
        o.Add("t", (int)MessageType.Pong);
        return Frame(o);
    }

    public static byte[] EncodeClipboardItem(ClipboardItem item)
    {
        var o = CBORObject.NewMap();
        o.Add("t", (int)MessageType.ClipboardItem);
        o.Add("seq", item.Seq);
        o.Add("origin_did", item.OriginDid);
        o.Add("ts_ms", item.TsMs);
        var fms = CBORObject.NewArray();
        foreach (var f in item.Formats)
        {
            var fo = CBORObject.NewMap();
            fo.Add("mime", f.Mime);
            fo.Add("size", f.Size);
            if (f.Inline is { } inl) fo.Add("inline", inl);
            else if (f.StreamId is { } sid) fo.Add("stream_id", sid);
            fms.Add(fo);
        }
        o.Add("formats", fms);
        if (item.Hint is { } h) o.Add("hint", h);
        return Frame(o);
    }

    public static byte[] EncodeFileChunk(ulong streamId, ulong offset, ReadOnlySpan<byte> data)
    {
        var o = CBORObject.NewMap();
        o.Add("t", (int)MessageType.FileChunk);
        o.Add("stream_id", streamId);
        o.Add("offset", offset);
        o.Add("data", data.ToArray());
        return Frame(o);
    }

    public static byte[] EncodeFileEnd(ulong streamId, ulong totalSize, byte[] sha256)
    {
        var o = CBORObject.NewMap();
        o.Add("t", (int)MessageType.FileEnd);
        o.Add("stream_id", streamId);
        o.Add("total_size", totalSize);
        o.Add("sha256", sha256);
        return Frame(o);
    }

    public static (ulong StreamId, ulong Offset, byte[] Data)? DecodeFileChunk(CBORObject body)
    {
        if (TypeOf(body) != MessageType.FileChunk) return null;
        return (body["stream_id"].ToObject<ulong>(), body["offset"].ToObject<ulong>(), body["data"].GetByteString());
    }

    public static (ulong StreamId, ulong TotalSize, byte[] Sha256)? DecodeFileEnd(CBORObject body)
    {
        if (TypeOf(body) != MessageType.FileEnd) return null;
        return (body["stream_id"].ToObject<ulong>(), body["total_size"].ToObject<ulong>(), body["sha256"].GetByteString());
    }

    /// Capabilities from a Hello; empty if absent.
    public static HashSet<string> DecodeHelloCaps(CBORObject body)
    {
        var caps = new HashSet<string>(StringComparer.Ordinal);
        if (body.ContainsKey("caps"))
            foreach (var c in body["caps"].Values) caps.Add(c.AsString());
        return caps;
    }

    public static MessageType? TypeOf(CBORObject body) =>
        body.ContainsKey("t") ? (MessageType)body["t"].AsInt32() : null;

    public static ClipboardItem? DecodeClipboardItem(CBORObject body)
    {
        if (TypeOf(body) != MessageType.ClipboardItem) return null;
        var formats = new List<ClipFormat>();
        foreach (var fo in body["formats"].Values)
        {
            var mime = fo["mime"].AsString();
            var size = fo["size"].ToObject<ulong>();
            byte[]? inl = fo.ContainsKey("inline") ? fo["inline"].GetByteString() : null;
            ulong? sid = fo.ContainsKey("stream_id") ? fo["stream_id"].ToObject<ulong>() : null;
            formats.Add(new ClipFormat(mime, size, inl, sid));
        }
        return new ClipboardItem(
            body["seq"].ToObject<ulong>(),
            body["origin_did"].GetByteString(),
            body["ts_ms"].ToObject<ulong>(),
            formats,
            body.ContainsKey("hint") ? body["hint"].AsString() : null);
    }
}
