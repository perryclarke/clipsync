using System;
using System.Collections.Generic;
using System.IO;
using PeterO.Cbor;
using System.Security.Cryptography;

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

public sealed record ClipFormat(string Mime, ulong Size, byte[]? Inline, ulong? StreamId);

public sealed record ClipboardItem(
    ulong Seq,
    byte[] OriginDid,
    ulong TsMs,
    List<ClipFormat> Formats,
    string? Hint)
{
    /// <summary>Canonical hash used for loop suppression. Must match
    /// the macOS implementation byte-for-byte.</summary>
    public byte[] CanonicalHash()
    {
        using var sha = SHA256.Create();
        var sorted = new List<ClipFormat>(Formats);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Mime, b.Mime));
        var ms = new MemoryStream();
        foreach (var f in sorted)
        {
            var mb = System.Text.Encoding.UTF8.GetBytes(f.Mime);
            ms.Write(mb, 0, mb.Length);
            ms.WriteByte(0);
            if (f.Inline is { } inl) ms.Write(inl, 0, inl.Length);
            ms.WriteByte(0xff);
        }
        return sha.ComputeHash(ms.ToArray());
    }
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
