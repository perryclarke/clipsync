using System;
using System.IO;
using ClipSync.Settings;
using Xunit;

namespace ClipSync.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "clipsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFileYieldsEmptyList()
    {
        var s = AppSettings.Load(_path);
        Assert.Empty(s.Excluded);
    }

    [Fact]
    public void AddedAppSurvivesRoundTrip()
    {
        var app = new AppIdentity(AppKind.Exe, @"C:\Program Files\KeePassXC\KeePassXC.exe", "KeePassXC",
                                  @"C:\Program Files\KeePassXC\KeePassXC.exe");
        AppSettings.Load(_path).Add(app);

        var reloaded = AppSettings.Load(_path);
        Assert.True(reloaded.IsExcluded(app));
        var entry = Assert.Single(reloaded.Excluded);
        Assert.Equal("keepassxc.exe", entry.Key);
        Assert.Equal("KeePassXC", entry.DisplayName);
        Assert.Equal(@"C:\Program Files\KeePassXC\KeePassXC.exe", entry.Path);
    }

    [Fact]
    public void PackageAppSurvivesRoundTrip()
    {
        var app = new AppIdentity(AppKind.Package, "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Windows Terminal");
        AppSettings.Load(_path).Add(app);

        var reloaded = AppSettings.Load(_path);
        Assert.True(reloaded.IsExcluded(app));
        Assert.Equal(AppKind.Package, Assert.Single(reloaded.Excluded).Kind);
    }

    [Fact]
    public void AddIsIdempotent()
    {
        var s = AppSettings.Load(_path);
        var app = new AppIdentity(AppKind.Exe, "notepad.exe", "Notepad");
        s.Add(app);
        s.Add(new AppIdentity(AppKind.Exe, @"C:\Windows\NOTEPAD.EXE", "Notepad"));
        Assert.Single(s.Excluded);
    }

    [Fact]
    public void RemoveDeletesTheEntryAndPersists()
    {
        var s = AppSettings.Load(_path);
        var app = new AppIdentity(AppKind.Exe, "notepad.exe", "Notepad");
        s.Add(app);
        s.Remove(app);

        Assert.Empty(s.Excluded);
        Assert.Empty(AppSettings.Load(_path).Excluded);
    }

    [Fact]
    public void RemovingAnAbsentAppIsANoOp()
    {
        var s = AppSettings.Load(_path);
        s.Remove(new AppIdentity(AppKind.Exe, "nothere.exe", "Nope"));
        Assert.Empty(s.Excluded);
    }

    [Fact]
    public void CorruptFileYieldsEmptyListInsteadOfThrowing()
    {
        File.WriteAllText(_path, "{ this is not json");
        var s = AppSettings.Load(_path);
        Assert.Empty(s.Excluded);
    }

    [Fact]
    public void UnknownKindEntriesAreIgnored()
    {
        // A settings file written by the future macOS build must degrade,
        // not break, on Windows.
        File.WriteAllText(_path, """
        {
          "version": 1,
          "excludedApps": [
            { "kind": "bundle", "key": "com.apple.Notes", "name": "Notes" },
            { "kind": "exe", "key": "notepad.exe", "name": "Notepad" }
          ]
        }
        """);

        var s = AppSettings.Load(_path);
        var only = Assert.Single(s.Excluded);
        Assert.Equal("notepad.exe", only.Key);
    }

    [Fact]
    public void EntriesMissingRequiredFieldsAreIgnored()
    {
        File.WriteAllText(_path, """
        { "version": 1, "excludedApps": [ { "kind": "exe", "name": "No key" } ] }
        """);
        Assert.Empty(AppSettings.Load(_path).Excluded);
    }

    [Fact]
    public void IsExcludedMatchesRegardlessOfDisplayNameAndDirectory()
    {
        var s = AppSettings.Load(_path);
        s.Add(new AppIdentity(AppKind.Exe, @"C:\old\Discord.exe", "Discord", @"C:\old\Discord.exe"));

        var seenAtRuntime = new AppIdentity(AppKind.Exe, @"C:\new\discord.exe", "Discord");
        Assert.True(s.IsExcluded(seenAtRuntime));
    }

    private const string Did = "b6bf89d9aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void HiddenPeerSurvivesRoundTripAndMatchesCaseInsensitively()
    {
        AppSettings.Load(_path).Hide(Did.ToUpperInvariant(), "Stranger");

        var reloaded = AppSettings.Load(_path);
        Assert.True(reloaded.IsHidden(Did));
        var entry = Assert.Single(reloaded.Hidden);
        Assert.Equal(Did, entry.DidHex);
        Assert.Equal("Stranger", entry.Name);
    }

    [Fact]
    public void HideIsIdempotentAndKeepsTheFirstName()
    {
        var s = AppSettings.Load(_path);
        s.Hide(Did, "First");
        s.Hide(Did, "Second");
        Assert.Equal("First", Assert.Single(s.Hidden).Name);
    }

    [Fact]
    public void UnhideRemovesTheEntryAndPersists()
    {
        var s = AppSettings.Load(_path);
        s.Hide(Did, "Stranger");
        s.Unhide(Did);

        Assert.False(s.IsHidden(Did));
        Assert.Empty(AppSettings.Load(_path).Hidden);
    }

    [Fact]
    public void HidingWithABlankNameFallsBackToTheFingerprint()
    {
        var s = AppSettings.Load(_path);
        s.Hide(Did, "  ");
        Assert.Equal(Did[..8], Assert.Single(s.Hidden).Name);
    }

    [Fact]
    public void ResetAllEmptiesEverythingAndPersists()
    {
        var s = AppSettings.Load(_path);
        s.Add(new AppIdentity(AppKind.Exe, "notepad.exe", "Notepad"));
        s.SetPeerPaused(Did, true);
        s.Hide(Did, "Stranger");

        s.ResetAll();

        Assert.Empty(s.Excluded);
        Assert.Empty(s.PausedPeers);
        Assert.Empty(s.Hidden);
        var reloaded = AppSettings.Load(_path);
        Assert.Empty(reloaded.Excluded);
        Assert.Empty(reloaded.PausedPeers);
        Assert.Empty(reloaded.Hidden);
    }
}
