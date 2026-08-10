using ClipSync.Settings;
using Xunit;

namespace ClipSync.Tests;

public class AppIdentityTests
{
    [Fact]
    public void ExeIdentitiesMatchOnFileNameIgnoringDirectoryAndCase()
    {
        // The whole point of file-name matching: a versioned install
        // directory changing on auto-update must not un-exclude the app.
        var a = new AppIdentity(AppKind.Exe, @"C:\Users\me\AppData\Local\Discord\app-1.0.9\Discord.exe", "Discord");
        var b = new AppIdentity(AppKind.Exe, @"C:\Users\me\AppData\Local\Discord\app-1.0.10\discord.EXE", "Discord");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DisplayNameAndPathDoNotAffectEquality()
    {
        var a = new AppIdentity(AppKind.Exe, "keepassxc.exe", "KeePassXC", @"C:\Program Files\KeePassXC\KeePassXC.exe");
        var b = new AppIdentity(AppKind.Exe, "keepassxc.exe", "renamed", null);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentKindsWithSameKeyAreNotEqual()
    {
        var exe = new AppIdentity(AppKind.Exe, "thing", "Thing");
        var pkg = new AppIdentity(AppKind.Package, "thing", "Thing");
        Assert.NotEqual(exe, pkg);
    }

    [Fact]
    public void PackageKeysCompareCaseInsensitively()
    {
        var a = new AppIdentity(AppKind.Package, "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Windows Terminal");
        var b = new AppIdentity(AppKind.Package, "microsoft.windowsterminal_8WEKYB3D8BBWE", "Windows Terminal");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ExeKeyIsNormalisedToBareFileName()
    {
        var a = new AppIdentity(AppKind.Exe, @"C:\Program Files\KeePassXC\KeePassXC.exe", "KeePassXC");
        Assert.Equal("keepassxc.exe", a.Key);
    }

    [Fact]
    public void PathIsPreservedVerbatimForDisplay()
    {
        const string p = @"C:\Program Files\KeePassXC\KeePassXC.exe";
        var a = new AppIdentity(AppKind.Exe, p, "KeePassXC", p);
        Assert.Equal(p, a.Path);
    }
}
