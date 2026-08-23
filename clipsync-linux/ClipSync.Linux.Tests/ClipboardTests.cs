using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClipSync.Clipboard;
using ClipSync.Net;
using ClipSync.Platform;
using ClipSync.Settings;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The clipboard rules that can be checked without an X server: which
/// formats get picked out of a messy target list, and whether our own
/// writes are recognised on the way back.
public class ClipboardFormatTests
{
    [Fact]
    public void PathFromUriListLine_DecodesFileUris()
    {
        Assert.Equal("/home/perry/a b.txt",
            ClipboardFormats.PathFromUriListLine("file:///home/perry/a%20b.txt"));
    }

    [Theory]
    [InlineData("# comment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com/x")]      // not a local file
    public void PathFromUriListLine_IgnoresWhatIsNotALocalFile(string line)
        => Assert.Null(ClipboardFormats.PathFromUriListLine(line));

    /// Older X clients ask for UTF8_STRING or STRING and never look for a
    /// MIME-typed target, so text must be offered under those names too.
    [Fact]
    public void OfferAliases_CoversLegacyTextTargets()
    {
        var aliases = ClipboardFormats.OfferAliases("text/plain;charset=utf-8");
        Assert.Contains("UTF8_STRING", aliases);
        Assert.Contains("STRING", aliases);
        Assert.Contains("TEXT", aliases);
        Assert.Contains("text/plain", aliases);
    }

    [Fact]
    public void OfferAliases_LeavesNonTextTypesAlone()
        => Assert.Equal(new[] { "image/png" }, ClipboardFormats.OfferAliases("image/png").ToArray());
}

public class ClipboardWatcherTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// Settings rooted in a throwaway file: these tests are about format
    /// selection, not exclusions, and must not read the developer's own.
    private static AppSettings TempSettings() => AppSettings.Load(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                               $"clipsync-test-{Guid.NewGuid():N}.json"));

    /// The real-world case measured on this machine: one image copied in
    /// Firefox offered twenty targets, ten of them empty, three of them the
    /// same bitmap under different names, and the WebP data mislabelled
    /// `audio/x-riff`. Exactly one text and one image format should survive.
    [Fact]
    public void MessyTargetList_ReducesToCanonicalFormats()
    {
        var offer = new FakeOffer(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = Utf8("hello"),
            ["UTF8_STRING"] = Utf8("hello"),
            ["STRING"] = Utf8("hello\0"),
            ["TEXT"] = Utf8("hello\0"),
            ["text/html"] = Utf8("<b>hello</b>"),
            ["image/png"] = [1, 2, 3],
            ["image/bmp"] = [9, 9, 9],
            ["image/x-bmp"] = [9, 9, 9],
            ["image/x-MS-bmp"] = [9, 9, 9],
            ["audio/x-riff"] = [7, 7],
            ["image/webp"] = [7, 7],
            ["image/tiff"] = [],
            ["image/x-icon"] = [],
            ["text/_moz_htmlcontext"] = [],
        });

        var item = Capture(offer);

        Assert.NotNull(item);
        Assert.Equal(
            new[] { "text/plain;charset=utf-8", "text/html", "image/png" },
            item!.Formats.Select(f => f.Mime).ToArray());
        Assert.Equal(Utf8("hello"), item.Formats[0].Inline);
    }

    /// An X client can advertise a target and then produce nothing for it.
    /// The next alias must be tried rather than the format being dropped.
    [Fact]
    public void EmptyPreferredTarget_FallsThroughToTheNextAlias()
    {
        var offer = new FakeOffer(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = [],
            ["UTF8_STRING"] = Utf8("fallback worked"),
        });

        var item = Capture(offer);

        Assert.NotNull(item);
        var format = Assert.Single(item!.Formats);
        Assert.Equal("text/plain;charset=utf-8", format.Mime);
        Assert.Equal(Utf8("fallback worked"), format.Inline);
    }

    [Fact]
    public void NothingUsable_ProducesNoItem()
    {
        var offer = new FakeOffer(new Dictionary<string, byte[]>
        {
            ["image/tiff"] = [],
            ["some/unknown-type"] = [1, 2, 3],
        });

        Assert.Null(Capture(offer));
    }

    /// Copied files travel as one path-only format each, matching what the
    /// Windows client produces from StorageItems.
    [Fact]
    public void UriList_BecomesOneFileUrlFormatPerFile()
    {
        var offer = new FakeOffer(new Dictionary<string, byte[]>
        {
            ["text/uri-list"] = Utf8("file:///tmp/one.txt\nfile:///tmp/two%20b.txt\n#comment\n"),
        });

        var item = Capture(offer);

        Assert.NotNull(item);
        Assert.Equal(2, item!.Formats.Count);
        Assert.All(item.Formats, f => Assert.Equal(ClipboardFormats.FileUrlMime, f.Mime));
        Assert.Equal("/tmp/one.txt", Encoding.UTF8.GetString(item.Formats[0].Inline!));
        Assert.Equal("/tmp/two b.txt", Encoding.UTF8.GetString(item.Formats[1].Inline!));
    }

    [Fact]
    public void Hint_IsTheFirstTextFormatTruncated()
    {
        var offer = new FakeOffer(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = Utf8(new string('x', 200)),
        });

        Assert.Equal(80, Capture(offer)!.Hint!.Length);
    }

    [Fact]
    public void SeqIncrementsPerCopy()
    {
        var backend = new FakeBackend();
        var writer = new ClipboardWriter(backend);
        var watcher = new ClipboardWatcher(backend, writer, new byte[32], TempSettings());
        var seen = new List<ClipboardItem>();
        watcher.OnLocalCopy = seen.Add;
        watcher.Start();

        var offer = new FakeOffer(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = Utf8("a"),
        });
        backend.Raise(offer);
        backend.Raise(offer);

        Assert.Equal(new ulong[] { 1, 2 }, seen.Select(i => i.Seq).ToArray());
    }

    /// An excluded app's copy stays local: it is still on this machine's
    /// clipboard, it just never reaches a peer.
    [Fact]
    public void CopyFromAnExcludedApp_IsNotTransmitted()
    {
        var settings = TempSettings();
        settings.Add(new AppIdentity(AppKind.Exe, "Signal", "Signal"));

        Assert.Null(Capture(TextFrom("secret", sourceApp: "Signal"), settings));
    }

    [Fact]
    public void CopyFromAnAppThatIsNotExcluded_IsTransmitted()
    {
        var settings = TempSettings();
        settings.Add(new AppIdentity(AppKind.Exe, "Signal", "Signal"));

        Assert.NotNull(Capture(TextFrom("fine", sourceApp: "Firefox"), settings));
    }

    /// Wayland-native copies arrive owned by the compositor's bridge, so the
    /// source is unknowable. Failing open is deliberate and matches the
    /// other two platforms: silently not syncing is worse than a rare miss.
    [Fact]
    public void CopyFromAnUnidentifiableApp_IsTransmitted()
    {
        var settings = TempSettings();
        settings.Add(new AppIdentity(AppKind.Exe, "Signal", "Signal"));

        Assert.NotNull(Capture(TextFrom("who copied this?", sourceApp: null), settings));
    }

    /// WM_CLASS casing varies by toolkit; AppIdentity lowercases its key, so
    /// an exclusion added as "Signal" must still match a window reporting
    /// "signal".
    [Fact]
    public void ExclusionMatching_IgnoresCase()
    {
        var settings = TempSettings();
        settings.Add(new AppIdentity(AppKind.Exe, "Signal", "Signal"));

        Assert.Null(Capture(TextFrom("secret", sourceApp: "signal"), settings));
    }

    private static FakeOffer TextFrom(string text, string? sourceApp) =>
        new(new Dictionary<string, byte[]>
        {
            ["text/plain;charset=utf-8"] = Utf8(text),
        }, sourceApp);

    private static ClipboardItem? Capture(IClipboardOffer offer, AppSettings? settings = null)
    {
        var backend = new FakeBackend();
        var writer = new ClipboardWriter(backend);
        var watcher = new ClipboardWatcher(backend, writer, new byte[32], settings ?? TempSettings());
        ClipboardItem? captured = null;
        watcher.OnLocalCopy = i => captured = i;
        watcher.Start();
        backend.Raise(offer);
        return captured;
    }
}

public class ClipboardWriterTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static ClipboardItem Item(params ClipFormat[] formats)
        => new(1, new byte[32], 0, formats.ToList(), null);

    [Fact]
    public void Apply_PublishesEveryAliasSoLegacyClientsCanPaste()
    {
        var backend = new FakeBackend();
        new ClipboardWriter(backend).Apply(
            Item(new ClipFormat("text/plain;charset=utf-8", 2, Utf8("hi"), null)));

        Assert.NotNull(backend.LastWrite);
        Assert.Equal(Utf8("hi"), backend.LastWrite!["UTF8_STRING"]);
        Assert.Equal(Utf8("hi"), backend.LastWrite["STRING"]);
    }

    /// File contents are not transferred on any platform, so a path from
    /// another machine must not be published as if it were local.
    [Fact]
    public void Apply_SkipsFileUrls()
    {
        var backend = new FakeBackend();
        new ClipboardWriter(backend).Apply(
            Item(new ClipFormat(ClipboardFormats.FileUrlMime, 5, Utf8("/x/y"), null)));

        Assert.Null(backend.LastWrite);      // nothing writable, nothing published
    }

    /// A format that never materialised carries no bytes and must be
    /// ignored rather than published as empty.
    [Fact]
    public void Apply_IgnoresFormatsWithNoInlineBytes()
    {
        var backend = new FakeBackend();
        new ClipboardWriter(backend).Apply(
            Item(new ClipFormat("image/png", 999, null, 7)));

        Assert.Null(backend.LastWrite);
    }

    [Fact]
    public void ConsumeRecentWrite_RecognisesWhatWeJustWrote()
    {
        var backend = new FakeBackend();
        var writer = new ClipboardWriter(backend);
        var item = Item(new ClipFormat("text/plain;charset=utf-8", 2, Utf8("hi"), null));

        writer.Apply(item);

        Assert.True(writer.ConsumeRecentWrite(item.CanonicalHash()));
    }

    /// Consuming removes the marker, so genuinely re-copying the same text a
    /// moment later is still transmitted rather than swallowed as an echo.
    [Fact]
    public void ConsumeRecentWrite_OnlyMatchesOnce()
    {
        var backend = new FakeBackend();
        var writer = new ClipboardWriter(backend);
        var item = Item(new ClipFormat("text/plain;charset=utf-8", 2, Utf8("hi"), null));

        writer.Apply(item);

        Assert.True(writer.ConsumeRecentWrite(item.CanonicalHash()));
        Assert.False(writer.ConsumeRecentWrite(item.CanonicalHash()));
    }

    /// When the writer drops a format the clipboard cannot hold, what comes
    /// back is a *smaller* item with a different hash. Without stamping that
    /// reduced form too, our own write would echo back to the peer.
    [Fact]
    public void ConsumeRecentWrite_RecognisesTheReducedFormAfterDroppingFormats()
    {
        var backend = new FakeBackend();
        var writer = new ClipboardWriter(backend);

        var text = new ClipFormat("text/plain;charset=utf-8", 2, Utf8("hi"), null);
        var file = new ClipFormat(ClipboardFormats.FileUrlMime, 4, Utf8("/x/y"), null);
        writer.Apply(Item(text, file));

        // What the watcher will rebuild from the clipboard: text only.
        var rebuilt = Item(text);
        Assert.True(writer.ConsumeRecentWrite(rebuilt.CanonicalHash()));
    }

    [Fact]
    public void ConsumeRecentWrite_IgnoresUnrelatedContent()
    {
        var backend = new FakeBackend();
        var writer = new ClipboardWriter(backend);
        writer.Apply(Item(new ClipFormat("text/plain;charset=utf-8", 2, Utf8("hi"), null)));

        var other = Item(new ClipFormat("text/plain;charset=utf-8", 5, Utf8("other"), null));
        Assert.False(writer.ConsumeRecentWrite(other.CanonicalHash()));
    }
}

// ---- fakes ---------------------------------------------------------

internal sealed class FakeBackend : IClipboardBackend
{
    private Action<IClipboardOffer>? _onSelection;

    public string Name => "Fake";
    public Dictionary<string, byte[]>? LastWrite { get; private set; }

    public bool IsAvailable() => true;
    public void Start(Action<IClipboardOffer> onSelection) => _onSelection = onSelection;
    public void SetSelection(IReadOnlyDictionary<string, byte[]> data)
        => LastWrite = new Dictionary<string, byte[]>(data);
    public void Raise(IClipboardOffer offer) => _onSelection?.Invoke(offer);
    public void Dispose() { }
}

internal sealed class FakeOffer : IClipboardOffer
{
    private readonly Dictionary<string, byte[]> _data;

    public FakeOffer(Dictionary<string, byte[]> data, string? sourceApp = null)
    {
        _data = data;
        SourceApp = sourceApp;
    }

    public IReadOnlyList<string> MimeTypes => _data.Keys.ToList();
    public string? SourceApp { get; }
    public byte[]? Receive(string mimeType) => _data.TryGetValue(mimeType, out var b) ? b : null;
}
