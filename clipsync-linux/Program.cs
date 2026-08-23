using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClipSync.Clipboard;
using ClipSync.Net;
using ClipSync.Platform.Backends.X11;
using ClipSync.Security;
using ClipSync.Settings;
using ClipSync.Sync;
using ClipSync.Ui;

namespace ClipSync;

/// Entry point.
///
/// Until the tray and settings UI exist this is a console harness: it runs
/// the real discovery and transport stack and exposes the trust action over
/// stdin, which is enough to exercise two-sided TOFU against the mac and
/// Windows clients.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--debug") || args.Contains("-d")) Identity.EnableLogging();

        // Core's logging seam: route the linked files' diagnostics through
        // the same opt-in sink as everything else.
        Log.Sink = Identity.Log;

        // --reset runs before the identity is loaded, matching the other two
        // clients: it must take effect immediately at launch, and before the
        // store is read into memory.
        if (args.Contains("--reset"))
        {
            TrustStore.Load().Clear();
            Console.WriteLine("trust store cleared");
        }

        var identity = Identity.LoadOrCreate();
        var trust = TrustStore.Load();

        Console.WriteLine($"ClipSync {Identity.AppVersion} (linux)");
        Console.WriteLine($"  device id   {identity.DidHex}");
        Console.WriteLine($"  fingerprint {identity.DidHex[..8]}");
        Console.WriteLine($"  trusted     {trust.All().Count} peer(s)" +
                          (trust.IsEmpty ? " — advertising pend=1" : ""));
        foreach (var e in trust.All())
            Console.WriteLine($"    {e.DidHex[..8]}  {e.Name}");

        if (args.Contains("--identity-only")) return 0;
        if (args.Contains("--self-test")) return SelfTest(identity);

        var settings = AppSettings.Load();
        var pause = new SyncPause(settings);

        var peers = new PeerRegistry(identity.DidHex);
        await using var discovery = new Discovery(identity, trust, peers);

        // The send gate: one predicate folding the global pause and the
        // per-peer mutes together, so the registry never learns what a
        // pause is. Same shape as the mac's AppCoordinator.
        peers.ShouldSendTo = pause.ShouldSendTo;

        using var clipboard = new X11ClipboardBackend();
        var writer = new ClipboardWriter(clipboard);
        var watcher = new ClipboardWatcher(clipboard, writer, identity.Did, settings);
        watcher.OnLocalCopy = peers.Broadcast;

        var shutdown = new TaskCompletionSource();

        await using var tray = new TrayIcon();
        tray.Bind(
            state: () => new TrayState(peers.GetAll(), pause.GlobalPaused,
                                       pause.IsMuted, settings.Hidden),
            actions: new TrayActions(
                SetPaused: p => pause.GlobalPaused = p,
                Trust: did =>
                {
                    var peer = peers.GetAll().FirstOrDefault(p => p.DidHex == did);
                    trust.Add(did, peer?.Name ?? did[..8]);
                    discovery.ConnectToPeer(did);
                },
                SetMuted: pause.SetMuted,
                Hide: settings.Hide,
                Unhide: settings.Unhide,
                Quit: () => shutdown.TrySetResult()));

        peers.OnChange = list =>
        {
            tray.Rebuild();
            Console.WriteLine($"\n-- peers ({list.Count}) --");
            foreach (var p in list)
                Console.WriteLine($"   {p.DidHex[..8]}  {p.Name,-24} {p.State}" +
                                  (p.Version is { } v ? $"  v{v}" : ""));
            Console.Write("> ");
        };
        peers.OnRemoteItem = item =>
        {
            var origin = Convert.ToHexString(item.OriginDid)[..8].ToLowerInvariant();
            var formats = string.Join(", ", item.Formats.Select(f => $"{f.Mime} {f.Size}B"));
            Console.WriteLine($"\n<- item from {origin}: {formats}");
            Console.Write("> ");
            writer.Apply(item);
        };

        try
        {
            await discovery.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"discovery failed to start: {ex.Message}");
            return 1;
        }

        if (clipboard.IsAvailable())
        {
            watcher.Start();
            Console.WriteLine($"clipboard: {clipboard.Name}");
        }
        else
        {
            Console.WriteLine($"clipboard: {clipboard.Name} unavailable — sync is receive-only");
        }

        if (await tray.StartAsync())
        {
            Console.WriteLine("tray: registered with StatusNotifierWatcher");
        }
        else
        {
            // Not a failure of ours, and worth saying plainly: everything
            // else works, there is simply nothing drawing an icon.
            Console.WriteLine("tray: no StatusNotifierWatcher on the session bus — no icon will appear.");
            Console.WriteLine("      On GNOME, enable it with:");
            Console.WriteLine("      gnome-extensions enable ubuntu-appindicators@ubuntu.com");
        }

        Console.WriteLine($"\nlistening on port {discovery.Port}, browsing _clipsync._tcp");
        Console.WriteLine(Commands);
        Console.Write("> ");

        // Under systemd there is no stdin, so ReadLine returns immediately
        // and forever. Without this the service would start, read EOF, and
        // exit cleanly — looking for all the world like a successful run.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("(no terminal — running as a service; use the tray menu)");
            await shutdown.Task;
        }
        else
        {
            await Task.WhenAny(ConsoleLoop(discovery, peers, trust, pause, settings), shutdown.Task);
        }
        return 0;
    }

    /// Exercises the clipboard round trip without needing a peer: write an
    /// item as though it had arrived from one, read it back through a
    /// separate X connection, and confirm the watcher did not treat our own
    /// write as a fresh local copy.
    ///
    /// Covers the three things most likely to break silently — INCR on the
    /// write path, byte fidelity, and loop suppression — none of which the
    /// unit tests can reach because they need a real X server.
    private static int SelfTest(Identity identity)
    {
        using var backend = new X11ClipboardBackend();
        if (!backend.IsAvailable())
        {
            Console.Error.WriteLine("self-test: no X display");
            return 1;
        }

        var writer = new ClipboardWriter(backend);
        var watcher = new ClipboardWatcher(backend, writer, identity.Did,
                                           AppSettings.Load(SelfTestSettingsPath()));
        var echoes = 0;
        watcher.OnLocalCopy = _ => Interlocked.Increment(ref echoes);
        watcher.Start();
        Thread.Sleep(1000);

        var text = Encoding.UTF8.GetBytes("clipsync self-test");
        var html = Encoding.UTF8.GetBytes("<b>clipsync self-test</b>");
        // Over the 64 KiB inline threshold and over the 256 KiB INCR chunk,
        // so serving it exercises a multi-chunk transfer.
        var blob = new byte[700_000];
        new Random(99).NextBytes(blob);
        // A path-only format, which the writer must drop. That makes what
        // lands on the clipboard smaller than the item, which is exactly the
        // case the second suppression stamp exists for.
        var filePath = Encoding.UTF8.GetBytes("/tmp/not-transferred.txt");

        var item = new ClipboardItem(1, identity.Did,
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new List<ClipFormat>
            {
                new("text/plain;charset=utf-8", (ulong)text.Length, text, null),
                new("text/html", (ulong)html.Length, html, null),
                new("image/png", (ulong)blob.Length, blob, null),
                new(ClipboardFormats.FileUrlMime, (ulong)filePath.Length, filePath, null),
            },
            "clipsync self-test");

        writer.Apply(item);
        Thread.Sleep(2000);

        var failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) failures++;
        }

        using (var x = XSelection.Connect())
        {
            var targets = x.ReadTargets();
            Console.WriteLine($"self-test: offered {targets.Length} target(s): {string.Join(", ", targets)}");

            Check("text round-trips",
                  x.ReadTarget("text/plain;charset=utf-8") is { } t && t.SequenceEqual(text));
            Check("html round-trips",
                  x.ReadTarget("text/html") is { } h && h.SequenceEqual(html));
            Check("700 KB image round-trips through INCR",
                  x.ReadTarget("image/png") is { } b && b.SequenceEqual(blob));
            Check("UTF8_STRING alias is offered for older clients",
                  targets.Contains("UTF8_STRING"));
            Check("file path was not published",
                  !targets.Contains(ClipboardFormats.FileUrlMime));
        }

        Check("our own write did not echo back as a local copy", echoes == 0);

        Console.WriteLine(failures == 0 ? "self-test: PASS" : $"self-test: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 2;
    }

    /// A throwaway settings file, so the self-test cannot be perturbed by
    /// (or perturb) the user's real exclusions.
    private static string SelfTestSettingsPath()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                  $"clipsync-selftest-{Guid.NewGuid():N}.json");

    private const string Commands =
        "commands: peers | status | trust <did> | pause | resume | mute <did> | unmute <did> |\n" +
        "          exclude <wm-class> | unexclude <wm-class> | quit";

    /// Stand-in for the tray menu: enough to click "Trust" from a terminal.
    private static async Task ConsoleLoop(Discovery discovery, PeerRegistry peers, TrustStore trust,
                                          SyncPause pause, AppSettings settings)
    {
        while (true)
        {
            var line = await Task.Run(Console.ReadLine);
            if (line is null) return;                      // stdin closed
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { Console.Write("> "); continue; }

            switch (parts[0])
            {
                case "quit" or "exit":
                    return;

                case "peers":
                    foreach (var p in peers.GetAll())
                        Console.WriteLine($"   {p.DidHex[..8]}  {p.Name,-24} {p.State}");
                    break;

                case "trust" when parts.Length > 1:
                {
                    // Match on a fingerprint prefix, the way the UI will —
                    // nobody is going to type 64 hex characters.
                    var prefix = parts[1].ToLowerInvariant();
                    var match = peers.GetAll().FirstOrDefault(p => p.DidHex.StartsWith(prefix));
                    if (match is null)
                    {
                        Console.WriteLine($"no discovered peer starts with {prefix}");
                        break;
                    }
                    trust.Add(match.DidHex, match.Name);
                    Console.WriteLine($"trusted {match.DidHex[..8]} ({match.Name}), connecting…");
                    discovery.ConnectToPeer(match.DidHex);
                    break;
                }

                case "pause":
                    pause.GlobalPaused = true;
                    Console.WriteLine("sending paused (this is deliberately NOT remembered across restarts)");
                    break;

                case "resume":
                    pause.GlobalPaused = false;
                    Console.WriteLine("sending resumed");
                    break;

                case "mute" when parts.Length > 1:
                case "unmute" when parts.Length > 1:
                {
                    var mute = parts[0] == "mute";
                    var match = peers.GetAll().FirstOrDefault(
                        p => p.DidHex.StartsWith(parts[1].ToLowerInvariant()));
                    if (match is null) { Console.WriteLine("no such peer"); break; }
                    pause.SetMuted(match.DidHex, mute);
                    Console.WriteLine($"{(mute ? "muted" : "unmuted")} {match.Name} (remembered across restarts)");
                    break;
                }

                case "exclude" when parts.Length > 1:
                {
                    // Keyed on WM_CLASS; see SelectionSource for why this is
                    // stored as an Exe entry.
                    var app = new AppIdentity(AppKind.Exe, parts[1], parts[1]);
                    settings.Add(app);
                    Console.WriteLine($"excluded {app.Key} — copies from it stay local");
                    break;
                }

                case "unexclude" when parts.Length > 1:
                {
                    var app = new AppIdentity(AppKind.Exe, parts[1], parts[1]);
                    settings.Remove(app);
                    Console.WriteLine($"no longer excluding {app.Key}");
                    break;
                }

                case "status":
                    Console.WriteLine($"   sending    {(pause.GlobalPaused ? "PAUSED" : "active")}");
                    Console.WriteLine($"   muted      {(pause.MutedPeers.Count == 0 ? "none" : string.Join(", ", pause.MutedPeers.Select(d => d[..8])))}");
                    Console.WriteLine($"   excluded   {(settings.Excluded.Count == 0 ? "none" : string.Join(", ", settings.Excluded.Select(a => a.Key)))}");
                    break;

                default:
                    Console.WriteLine(Commands);
                    break;
            }
            Console.Write("> ");
        }
    }
}
