using System;
using System.IO;
using ClipSync.Platform;
using Xunit;

namespace ClipSync.Linux.Tests;

/// The start-at-login switch's file logic, against temp stand-ins for
/// ~/.config/autostart and /etc/xdg/autostart. The interplay matters: the
/// .deb installs a system-wide entry, so "off" must be an override file,
/// not a deletion it has no rights to make.
public class AutostartTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("clipsync-autostart-").FullName;
    private readonly string _userDir;
    private readonly string _systemFile;

    public AutostartTests()
    {
        _userDir = Path.Combine(_root, "user");
        var systemDir = Path.Combine(_root, "system");
        Directory.CreateDirectory(systemDir);
        _systemFile = Path.Combine(systemDir, "clipsync.desktop");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Autostart Make() => new(_userDir, _systemFile);

    private void InstallSystemEntry()
        => File.WriteAllText(_systemFile, "[Desktop Entry]\nType=Application\nName=ClipSync\n");

    [Fact]
    public void NoFilesAnywhere_MeansDisabled()
        => Assert.False(Make().IsEnabled());

    [Fact]
    public void SystemEntryAlone_MeansEnabled_AsTheDebInstallDoes()
    {
        InstallSystemEntry();
        Assert.True(Make().IsEnabled());
    }

    [Fact]
    public void DisablingOverPackagedInstall_WritesAHiddenOverride()
    {
        InstallSystemEntry();
        var autostart = Make();

        autostart.SetEnabled(false);

        Assert.False(autostart.IsEnabled());
        Assert.Contains("Hidden=true",
                        File.ReadAllText(Path.Combine(_userDir, "clipsync.desktop")));
        Assert.True(File.Exists(_systemFile));
    }

    [Fact]
    public void ReenablingOverPackagedInstall_RemovesTheOverride()
    {
        InstallSystemEntry();
        var autostart = Make();
        autostart.SetEnabled(false);

        autostart.SetEnabled(true);

        Assert.True(autostart.IsEnabled());
        Assert.False(File.Exists(Path.Combine(_userDir, "clipsync.desktop")));
    }

    [Fact]
    public void EnablingWithoutSystemEntry_WritesARealUserEntry()
    {
        var autostart = Make();

        autostart.SetEnabled(true);

        Assert.True(autostart.IsEnabled());
        var entry = File.ReadAllText(Path.Combine(_userDir, "clipsync.desktop"));
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Exec=", entry);
        Assert.DoesNotContain("Hidden=true", entry);
    }

    [Fact]
    public void DisablingWithoutSystemEntry_DeletesTheUserEntry()
    {
        var autostart = Make();
        autostart.SetEnabled(true);

        autostart.SetEnabled(false);

        Assert.False(autostart.IsEnabled());
        Assert.False(File.Exists(Path.Combine(_userDir, "clipsync.desktop")));
    }

    [Fact]
    public void TogglingIsIdempotent()
    {
        InstallSystemEntry();
        var autostart = Make();

        autostart.SetEnabled(false);
        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled());

        autostart.SetEnabled(true);
        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled());
    }
}
