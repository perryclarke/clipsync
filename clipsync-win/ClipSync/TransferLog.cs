using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using ClipSync.Net;

namespace ClipSync;

/// Append-only log of clipboard items the app has sent or received. Used
/// by the test tools to verify that a fuzzed local copy actually crossed
/// the wire. Lines match the mac format so the two logs are diffable:
/// `<iso8601-utc> <SEND|RECV> <bytes> <sha8> "<hint>"`
public static class TransferLog
{
    private static readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly string _path = Init();

    public static void Send(ClipboardItem item) => Write("SEND", item);
    public static void Recv(ClipboardItem item) => Write("RECV", item);

    private static string Init()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipSync");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "transfers.log");
        _ = Task.Run(() => Pump(path));
        return path;
    }

    private static void Write(string direction, ClipboardItem item)
    {
        ulong total = 0;
        foreach (var f in item.Formats) total += f.Size;
        var hash = item.CanonicalHash();
        var sha8 = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
        var hint = Escape(item.Hint ?? "");
        var line = $"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")} {direction} {total} {sha8} \"{hint}\"\n";
        _channel.Writer.TryWrite(line);
    }

    private static async Task Pump(string path)
    {
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            try { await File.AppendAllTextAsync(path, line, Encoding.UTF8); } catch { }
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:   sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
