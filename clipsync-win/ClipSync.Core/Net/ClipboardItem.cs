using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ClipSync.Net;

// Wire-level item types, mirroring PROTOCOL.md §6.2. They live in Core so
// the pure send/receive planning around them (StreamPlanner,
// StreamAssembler) can be unit-tested; the CBOR codec stays in the app.

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

