using System;
using System.Threading;
using System.Windows.Forms;

// clipfuzz [count]
//
// Writes [count] clipboard items (default 20) at random intervals (3-20s).
// Each item is "<machine> <seq> <lorem ipsum>" where the lorem portion
// is 10-4000 chars, weighted so a length below 100 is 3x as likely as
// 100+. The machine name and sequence number make missed transfers
// easy to spot when correlating two peers' transfers.log files.

namespace ClipFuzz;

static class Program
{
    static readonly string[] Words =
    {
        "lorem","ipsum","dolor","sit","amet","consectetur","adipiscing","elit",
        "sed","do","eiusmod","tempor","incididunt","ut","labore","et","dolore",
        "magna","aliqua","enim","ad","minim","veniam","quis","nostrud",
        "exercitation","ullamco","laboris","nisi","aliquip","ex","ea","commodo",
        "consequat","duis","aute","irure","in","reprehenderit","voluptate",
        "velit","esse","cillum","eu","fugiat","nulla","pariatur","excepteur",
        "sint","occaecat","cupidatat","non","proident","sunt","culpa","qui",
        "officia","deserunt","mollit","anim","id","est","laborum"
    };

    static string RandomString(Random rng, int length)
    {
        var sb = new System.Text.StringBuilder(length + 16);
        while (sb.Length < length)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Words[rng.Next(Words.Length)]);
        }
        return sb.ToString(0, length);
    }

    /// Lorem length: low band [10..99] is 3x as likely as high band [100..4000].
    static int RandomLoremLength(Random rng) =>
        rng.Next(4) == 0 ? rng.Next(100, 4001) : rng.Next(10, 100);

    [STAThread]
    static int Main(string[] args)
    {
        int count = 20;
        if (args.Length >= 1)
        {
            if (!int.TryParse(args[0], out count) || count <= 0)
            {
                Console.Error.WriteLine("usage: clipfuzz [count]");
                return 2;
            }
        }

        var rng = new Random();
        var machine = Environment.MachineName;
        for (int seq = 1; seq <= count; seq++)
        {
            var lorem = RandomString(rng, RandomLoremLength(rng));
            var s = $"{machine} {seq} {lorem}";

            // Clipboard.SetText can fail transiently (clipboard locked by
            // another process); retry a few times.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetText(s); break; }
                catch { Thread.Sleep(50); }
            }

            var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var preview = s.Length > 60 ? s.Substring(0, 60) : s;
            preview = preview.Replace("\n", "\\n");
            Console.WriteLine($"{ts} WRITE {seq}/{count} len={System.Text.Encoding.UTF8.GetByteCount(s)} \"{preview}\"");

            if (seq < count)
            {
                var delayMs = (int)(rng.NextDouble() * 17000 + 3000);
                Thread.Sleep(delayMs);
            }
        }
        return 0;
    }
}
