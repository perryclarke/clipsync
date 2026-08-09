# Excluded Apps (Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user nominate installed applications whose clipboard activity ClipSync never transmits to peers.

**Architecture:** A new `ClipSync.Core` class library holds the three testable pieces — `AppIdentity`, `AppSettings`, `ForegroundRing`/`ForegroundTracker` — so the xunit project can reference them without loading WinUI. The app project references Core, `ClipboardWatcher` consults the tracker before broadcasting, and two new WinUI surfaces (a settings window and an app picker) edit the list.

**Tech Stack:** C# 13 / .NET 10, WinUI 3 (Windows App SDK 1.8), xunit, Win32 P/Invoke (`SetWinEventHook`), shell COM interop (`IShellItem`/`IEnumShellItems`), `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-08-09-excluded-apps-design.md`

## Global Constraints

- Target framework for **all** projects: `net10.0-windows10.0.26100.0`. Platform `x64`.
- `dotnet` is **not on PATH**. Every command uses `$dotnet = "C:\Program Files\dotnet\dotnet.exe"`.
- `<Nullable>enable</Nullable>` on every new project. The build currently emits 6 warnings; do not add more.
- **No new NuGet dependencies in the app or Core.** Test project gets xunit only.
- Logging goes through `ClipSync.Security.Identity.Log`. It must **never** contain clipboard content — display names, counts and metadata only.
- **Fail open:** any failure to determine the foreground app results in the item being transmitted.
- Exclusion suppresses **transmission only**. Never alter the local clipboard on this path.
- Commit after every task. Commit messages end with:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`

## Deviation from the spec (deliberate)

The spec placed the new types under `clipsync-win/ClipSync/Settings/` and `clipsync-win/ClipSync/Clipboard/`. This plan puts `AppIdentity`, `AppSettings`, `ForegroundRing` and `ForegroundTracker` in a new `clipsync-win/ClipSync.Core/` class library instead, because `ClipSync.csproj` is a `WinExe` with `UseWinUI=true` — referencing it from a test project drags in the Windows App SDK bootstrapper and makes `dotnet test` unreliable. Namespaces (`ClipSync.Settings`, `ClipSync.Clipboard`) are unchanged, so the spec's file table still describes the code accurately. Everything else follows the spec as written.

## File Structure

| File | Responsibility |
|---|---|
| `clipsync-win/ClipSync.Core/ClipSync.Core.csproj` | Class library, no WinUI |
| `clipsync-win/ClipSync.Core/Settings/AppIdentity.cs` | Value type naming an app; matching rules |
| `clipsync-win/ClipSync.Core/Settings/AppSettings.cs` | JSON persistence of the exclusion list |
| `clipsync-win/ClipSync.Core/Clipboard/ForegroundRing.cs` | Timestamped ring; pure logic |
| `clipsync-win/ClipSync.Core/Clipboard/IWindowResolver.cs` | HWND → `AppIdentity` seam for tests |
| `clipsync-win/ClipSync.Core/Clipboard/Win32WindowResolver.cs` | Real Win32/packaged-app resolution |
| `clipsync-win/ClipSync.Core/Clipboard/ForegroundTracker.cs` | `SetWinEventHook` + ring |
| `clipsync-win/ClipSync.Tests/ClipSync.Tests.csproj` | xunit project |
| `clipsync-win/ClipSync.Tests/AppIdentityTests.cs` | Task 1 tests |
| `clipsync-win/ClipSync.Tests/AppSettingsTests.cs` | Task 2 tests |
| `clipsync-win/ClipSync.Tests/ForegroundRingTests.cs` | Task 3 tests |
| `clipsync-win/ClipSync/UI/InstalledApps.cs` | `shell:AppsFolder` enumeration + icons |
| `clipsync-win/ClipSync/UI/SettingsWindow.xaml(.cs)` | Exclusion list UI |
| `clipsync-win/ClipSync/UI/AppPickerDialog.xaml(.cs)` | Search + pick + Browse |
| `clipsync-win/ClipSync/Clipboard/ClipboardWatcher.cs` | **Modified** — suppression check |
| `clipsync-win/ClipSync/App.xaml.cs` | **Modified** — construct settings + tracker |
| `clipsync-win/ClipSync/UI/TrayPopup.xaml(.cs)` | **Modified** — `Settings…` button |
| `clipsync-win/ClipSync.sln` | **Modified** — two new projects |

---

### Task 1: Project scaffolding and `AppIdentity`

**Files:**
- Create: `clipsync-win/ClipSync.Core/ClipSync.Core.csproj`
- Create: `clipsync-win/ClipSync.Core/Settings/AppIdentity.cs`
- Create: `clipsync-win/ClipSync.Tests/ClipSync.Tests.csproj`
- Create: `clipsync-win/ClipSync.Tests/AppIdentityTests.cs`
- Modify: `clipsync-win/ClipSync/ClipSync.csproj` (add `ProjectReference`)
- Modify: `clipsync-win/ClipSync.sln`

**Interfaces:**
- Consumes: nothing.
- Produces: `ClipSync.Settings.AppKind` (`Exe`, `Package`); `ClipSync.Settings.AppIdentity` with constructor `AppIdentity(AppKind kind, string key, string displayName, string? path = null)` and properties `Kind`, `Key`, `DisplayName`, `Path`. Equality is `Kind`+`Key` only. `Key` is normalised in the constructor: for `Exe` it becomes the lowercased **file name**; for `Package` the lowercased string as given.

- [ ] **Step 1: Create the Core project file**

Create `clipsync-win/ClipSync.Core/ClipSync.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.26100.0</SupportedOSPlatformVersion>
    <RootNamespace>ClipSync</RootNamespace>
    <Platforms>x64</Platforms>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create the test project file**

Create `clipsync-win/ClipSync.Tests/ClipSync.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.26100.0</SupportedOSPlatformVersion>
    <Platforms>x64</Platforms>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ClipSync.Core\ClipSync.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the failing test**

Create `clipsync-win/ClipSync.Tests/AppIdentityTests.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it fails**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj
```

Expected: FAIL — compile error, `AppIdentity` and `AppKind` do not exist.

- [ ] **Step 5: Implement `AppIdentity`**

Create `clipsync-win/ClipSync.Core/Settings/AppIdentity.cs`:

```csharp
using System;

namespace ClipSync.Settings;

public enum AppKind
{
    /// Desktop (Win32) application, keyed by executable file name.
    Exe,
    /// Packaged (Store/UWP) application, keyed by package family name.
    Package,
}

/// Names an application for exclusion purposes.
///
/// Equality is deliberately on Kind+Key only: DisplayName and Path are
/// presentation data, so an app that is renamed or moves to a new
/// versioned install directory still matches a saved exclusion.
public sealed record AppIdentity
{
    public AppKind Kind { get; }

    /// Normalised match key. Lowercased throughout; for Exe this is the
    /// bare file name, so `app-1.0.9\Discord.exe` and
    /// `app-1.0.10\Discord.exe` are the same app.
    public string Key { get; }

    public string DisplayName { get; }

    /// Full path the user originally picked, kept only so the UI can show
    /// which `javaw.exe` (or similar ambiguous name) this refers to.
    public string? Path { get; }

    public AppIdentity(AppKind kind, string key, string displayName, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
        Kind = kind;
        Key = Normalise(kind, key);
        DisplayName = displayName;
        Path = path;
    }

    private static string Normalise(AppKind kind, string key) => kind switch
    {
        AppKind.Exe => System.IO.Path.GetFileName(key.Trim()).ToLowerInvariant(),
        _ => key.Trim().ToLowerInvariant(),
    };

    public bool Equals(AppIdentity? other) =>
        other is not null && Kind == other.Kind && Key == other.Key;

    public override int GetHashCode() => HashCode.Combine(Kind, Key);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj
```

Expected: PASS, 6 tests.

- [ ] **Step 7: Reference Core from the app project**

In `clipsync-win/ClipSync/ClipSync.csproj`, add a new `ItemGroup` immediately after the `PackageReference` group:

```xml
  <ItemGroup>
    <ProjectReference Include="..\ClipSync.Core\ClipSync.Core.csproj" />
  </ItemGroup>
```

- [ ] **Step 8: Add both projects to the solution**

In `clipsync-win/ClipSync.sln`, add after the existing `EndProject` line:

```
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "ClipSync.Core", "ClipSync.Core\ClipSync.Core.csproj", "{3F2A7C41-5B8E-4D19-9E30-2C7A1B4D6E02}"
EndProject
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "ClipSync.Tests", "ClipSync.Tests\ClipSync.Tests.csproj", "{7D1E4A88-3C62-4F5B-8A11-9B0E5C2F7A03}"
EndProject
```

And inside `GlobalSection(ProjectConfigurationPlatforms)`, before its `EndGlobalSection`:

```
		{3F2A7C41-5B8E-4D19-9E30-2C7A1B4D6E02}.Debug|x64.ActiveCfg = Debug|x64
		{3F2A7C41-5B8E-4D19-9E30-2C7A1B4D6E02}.Debug|x64.Build.0 = Debug|x64
		{3F2A7C41-5B8E-4D19-9E30-2C7A1B4D6E02}.Release|x64.ActiveCfg = Release|x64
		{3F2A7C41-5B8E-4D19-9E30-2C7A1B4D6E02}.Release|x64.Build.0 = Release|x64
		{7D1E4A88-3C62-4F5B-8A11-9B0E5C2F7A03}.Debug|x64.ActiveCfg = Debug|x64
		{7D1E4A88-3C62-4F5B-8A11-9B0E5C2F7A03}.Debug|x64.Build.0 = Debug|x64
		{7D1E4A88-3C62-4F5B-8A11-9B0E5C2F7A03}.Release|x64.ActiveCfg = Release|x64
		{7D1E4A88-3C62-4F5B-8A11-9B0E5C2F7A03}.Release|x64.Build.0 = Release|x64
```

- [ ] **Step 9: Verify the app still builds**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync\ClipSync.csproj -c Debug -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` with 6 warnings (the pre-existing ones). No new warnings.

- [ ] **Step 10: Commit**

```bash
git add clipsync-win/ClipSync.Core clipsync-win/ClipSync.Tests clipsync-win/ClipSync/ClipSync.csproj clipsync-win/ClipSync.sln
git commit -m "Add ClipSync.Core and test project with AppIdentity

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: `AppSettings` persistence

**Files:**
- Create: `clipsync-win/ClipSync.Core/Settings/AppSettings.cs`
- Create: `clipsync-win/ClipSync.Tests/AppSettingsTests.cs`

**Interfaces:**
- Consumes: `AppIdentity`, `AppKind` from Task 1.
- Produces: `ClipSync.Settings.AppSettings` with `static string DefaultPath`, `static AppSettings Load()`, `static AppSettings Load(string path)`, `IReadOnlyList<AppIdentity> Excluded`, `bool IsExcluded(AppIdentity app)`, `void Add(AppIdentity app)`, `void Remove(AppIdentity app)`.

- [ ] **Step 1: Write the failing tests**

Create `clipsync-win/ClipSync.Tests/AppSettingsTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj --filter "FullyQualifiedName~AppSettingsTests"
```

Expected: FAIL — compile error, `AppSettings` does not exist.

- [ ] **Step 3: Implement `AppSettings`**

This references `ClipSync.Security.Log`, which Step 4 creates. Do not try
to build between these two steps — write both files, then build.

Create `clipsync-win/ClipSync.Core/Settings/AppSettings.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipSync.Settings;

/// User preferences, stored as plain JSON in %LOCALAPPDATA%\ClipSync.
/// Unlike the trust store this is not secret, and being hand-editable is
/// a feature. A corrupt file degrades to defaults rather than throwing,
/// matching TrustStore.Load().
public sealed class AppSettings
{
    private const int CurrentVersion = 1;

    private readonly string _path;
    private readonly List<AppIdentity> _excluded;
    private readonly object _lock = new();

    private AppSettings(string path, List<AppIdentity> excluded)
    {
        _path = path;
        _excluded = excluded;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipSync", "settings.json");

    public static AppSettings Load() => Load(DefaultPath);

    public static AppSettings Load(string path)
    {
        var list = new List<AppIdentity>();
        try
        {
            if (File.Exists(path))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions);
                foreach (var e in model?.ExcludedApps ?? new List<FileModel.Entry>())
                {
                    if (TryParse(e, out var id)) list.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable: start empty rather than crashing the app.
            Security.Log.Write($"AppSettings: could not read {path}: {ex.GetType().Name}; using defaults");
            list.Clear();
        }
        return new AppSettings(path, list);
    }

    public IReadOnlyList<AppIdentity> Excluded
    {
        get { lock (_lock) return _excluded.ToList(); }
    }

    public bool IsExcluded(AppIdentity app)
    {
        lock (_lock) return _excluded.Contains(app);
    }

    public void Add(AppIdentity app)
    {
        lock (_lock)
        {
            if (_excluded.Contains(app)) return;
            _excluded.Add(app);
            Persist();
        }
    }

    public void Remove(AppIdentity app)
    {
        lock (_lock)
        {
            if (_excluded.RemoveAll(e => e.Equals(app)) == 0) return;
            Persist();
        }
    }

    /// Caller holds _lock.
    private void Persist()
    {
        try
        {
            var model = new FileModel
            {
                Version = CurrentVersion,
                ExcludedApps = _excluded.Select(e => new FileModel.Entry
                {
                    Kind = e.Kind == AppKind.Exe ? "exe" : "package",
                    Key = e.Key,
                    Name = e.DisplayName,
                    Path = e.Path,
                }).ToList(),
            };

            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write-then-move so a crash mid-write cannot leave a corrupt file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // In-memory list keeps working for this session.
            Security.Log.Write($"AppSettings: could not write {_path}: {ex.GetType().Name}");
        }
    }

    private static bool TryParse(FileModel.Entry e, out AppIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(e.Key)) return false;

        AppKind kind;
        switch (e.Kind?.ToLowerInvariant())
        {
            case "exe": kind = AppKind.Exe; break;
            case "package": kind = AppKind.Package; break;
            default: return false;   // e.g. macOS "bundle" entries
        }

        identity = new AppIdentity(kind, e.Key!, e.Name ?? e.Key!, e.Path);
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class FileModel
    {
        public int Version { get; set; } = CurrentVersion;
        public List<Entry> ExcludedApps { get; set; } = new();

        public sealed class Entry
        {
            public string? Kind { get; set; }
            public string? Key { get; set; }
            public string? Name { get; set; }
            public string? Path { get; set; }
        }
    }
}
```

- [ ] **Step 4: Add the Core logging shim**

Core cannot reference `ClipSync.Security.Identity` (that lives in the app project, which references Core — the dependency runs the other way). Create `clipsync-win/ClipSync.Core/Security/Log.cs`:

```csharp
using System;

namespace ClipSync.Security;

/// Logging seam for Core. The app assigns Sink at startup so Core's
/// diagnostics land in the same opt-in debug.log as everything else.
/// Messages must never contain clipboard content.
public static class Log
{
    public static Action<string>? Sink;

    public static void Write(string message)
    {
        try { Sink?.Invoke(message); } catch { }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj --filter "FullyQualifiedName~AppSettingsTests"
```

Expected: PASS, 10 tests.

- [ ] **Step 6: Commit**

```bash
git add clipsync-win/ClipSync.Core clipsync-win/ClipSync.Tests
git commit -m "Add AppSettings JSON persistence for excluded apps

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: `ForegroundRing`

**Files:**
- Create: `clipsync-win/ClipSync.Core/Clipboard/ForegroundRing.cs`
- Create: `clipsync-win/ClipSync.Tests/ForegroundRingTests.cs`

**Interfaces:**
- Consumes: `AppIdentity` from Task 1.
- Produces: `ClipSync.Clipboard.ForegroundRing` with `const int MaxEntries = 16`, `static readonly TimeSpan MaxAge` (2 minutes), `void Record(DateTime atUtc, AppIdentity? app)`, `AppIdentity? AppAt(DateTime utc)`.

Semantics: each entry owns the half-open interval `[its timestamp, next entry's timestamp)`; the newest runs to infinity. A timestamp before the oldest retained entry returns `null`.

- [ ] **Step 1: Write the failing tests**

Create `clipsync-win/ClipSync.Tests/ForegroundRingTests.cs`:

```csharp
using System;
using ClipSync.Clipboard;
using ClipSync.Settings;
using Xunit;

namespace ClipSync.Tests;

public class ForegroundRingTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private static AppIdentity App(string name) => new(AppKind.Exe, name + ".exe", name);

    [Fact]
    public void EmptyRingReturnsNull()
    {
        Assert.Null(new ForegroundRing().AppAt(T0));
    }

    [Fact]
    public void TimestampBeforeOldestEntryReturnsNull()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        Assert.Null(ring.AppAt(T0.AddSeconds(-1)));
    }

    [Fact]
    public void TimestampExactlyOnTransitionResolvesToNewlyActivatedApp()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        ring.Record(T0.AddSeconds(10), App("b"));

        Assert.Equal(App("b"), ring.AppAt(T0.AddSeconds(10)));
    }

    [Fact]
    public void TimestampInsideAnIntervalResolvesToThatIntervalsApp()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        ring.Record(T0.AddSeconds(10), App("b"));

        Assert.Equal(App("a"), ring.AppAt(T0.AddSeconds(5)));
        Assert.Equal(App("a"), ring.AppAt(T0.AddSeconds(9.999)));
    }

    [Fact]
    public void NewestEntryExtendsIndefinitely()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        Assert.Equal(App("a"), ring.AppAt(T0.AddHours(3)));
    }

    [Fact]
    public void NullIdentityIsRecordedAndReturned()
    {
        // An unresolvable window (e.g. an elevated app) records as null,
        // which the caller treats as fail-open.
        var ring = new ForegroundRing();
        ring.Record(T0, null);
        Assert.Null(ring.AppAt(T0.AddSeconds(1)));
    }

    [Fact]
    public void RingKeepsAtMostMaxEntries()
    {
        var ring = new ForegroundRing();
        for (int i = 0; i <= ForegroundRing.MaxEntries; i++)
            ring.Record(T0.AddSeconds(i), App("app" + i));

        // The oldest was evicted, so its interval no longer resolves.
        Assert.Null(ring.AppAt(T0));
        Assert.Equal(App("app1"), ring.AppAt(T0.AddSeconds(1)));
    }

    [Fact]
    public void EntriesOlderThanMaxAgeAreEvicted()
    {
        var ring = new ForegroundRing();
        ring.Record(T0, App("old"));
        var later = T0 + ForegroundRing.MaxAge + TimeSpan.FromSeconds(1);
        ring.Record(later, App("new"));

        Assert.Null(ring.AppAt(T0.AddSeconds(1)));
        Assert.Equal(App("new"), ring.AppAt(later));
    }

    [Fact]
    public void SoleEntryIsNotEvictedByAgeAlone()
    {
        // A user who has stayed in one app for an hour must still resolve.
        var ring = new ForegroundRing();
        ring.Record(T0, App("a"));
        Assert.Equal(App("a"), ring.AppAt(T0 + TimeSpan.FromHours(1)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj --filter "FullyQualifiedName~ForegroundRingTests"
```

Expected: FAIL — compile error, `ForegroundRing` does not exist.

- [ ] **Step 3: Implement `ForegroundRing`**

Create `clipsync-win/ClipSync.Core/Clipboard/ForegroundRing.cs`:

```csharp
using System;
using System.Collections.Generic;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// A short, bounded history of which app was in the foreground and when.
///
/// Clipboard change notifications arrive asynchronously, so by the time
/// the watcher has an item the user may already have switched apps.
/// Recording transitions as they happen lets the watcher ask what was in
/// front at the moment of the copy rather than at the moment of handling.
public sealed class ForegroundRing
{
    public const int MaxEntries = 16;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();

    private readonly record struct Entry(DateTime At, AppIdentity? App);

    public void Record(DateTime atUtc, AppIdentity? app)
    {
        lock (_lock)
        {
            _entries.Add(new Entry(atUtc, app));
            Trim(atUtc);
        }
    }

    /// The app whose interval contains `utc`, or null if `utc` predates
    /// everything retained (which the caller treats as fail-open).
    public AppIdentity? AppAt(DateTime utc)
    {
        lock (_lock)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].At <= utc) return _entries[i].App;
            return null;
        }
    }

    /// Caller holds _lock. Always leaves at least the newest entry, so a
    /// user who has sat in one app for hours still resolves.
    private void Trim(DateTime nowUtc)
    {
        // Find the newest expired entry, then remove everything up to and
        // including it in one shot — removing inside the scan would
        // invalidate the indices we are still iterating over.
        var lastExpired = -1;
        for (var i = 0; i < _entries.Count - 1; i++)
            if (nowUtc - _entries[i].At > MaxAge) lastExpired = i;
        if (lastExpired >= 0) _entries.RemoveRange(0, lastExpired + 1);

        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj --filter "FullyQualifiedName~ForegroundRingTests"
```

Expected: PASS, 9 tests.

- [ ] **Step 5: Run the whole suite**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj
```

Expected: PASS, 25 tests.

- [ ] **Step 6: Commit**

```bash
git add clipsync-win/ClipSync.Core clipsync-win/ClipSync.Tests
git commit -m "Add ForegroundRing timestamped foreground history

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: `ForegroundTracker` — Win32 hook and app resolution

**Files:**
- Create: `clipsync-win/ClipSync.Core/Clipboard/IWindowResolver.cs`
- Create: `clipsync-win/ClipSync.Core/Clipboard/Win32WindowResolver.cs`
- Create: `clipsync-win/ClipSync.Core/Clipboard/ForegroundTracker.cs`

**Interfaces:**
- Consumes: `ForegroundRing`, `AppIdentity`, `AppKind`, `Security.Log`.
- Produces: `ClipSync.Clipboard.IWindowResolver` with `IntPtr GetForegroundWindow()` and `AppIdentity? Resolve(IntPtr hwnd)`; `ClipSync.Clipboard.Win32WindowResolver : IWindowResolver`; `ClipSync.Clipboard.ForegroundTracker` with `ForegroundTracker()`, `ForegroundTracker(IWindowResolver resolver)`, `void Start()`, `void Stop()`, `AppIdentity? AppAt(DateTime utc)`.

No unit tests: this task is entirely OS interop, verified manually in Task 9. The ring it depends on is already covered.

- [ ] **Step 1: Define the resolver seam**

Create `clipsync-win/ClipSync.Core/Clipboard/IWindowResolver.cs`:

```csharp
using System;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Isolates the Win32 calls so ForegroundTracker's logic is testable and
/// so a resolution failure has one obvious place to live.
public interface IWindowResolver
{
    IntPtr GetForegroundWindow();

    /// The app owning `hwnd`, or null if it cannot be determined
    /// (elevated process, race with window teardown, etc.).
    AppIdentity? Resolve(IntPtr hwnd);
}
```

- [ ] **Step 2: Implement the Win32 resolver**

Create `clipsync-win/ClipSync.Core/Clipboard/Win32WindowResolver.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Resolves a window handle to the application that owns it.
///
/// The awkward part is Store apps: their foreground window belongs to
/// ApplicationFrameHost.exe, not to the app. The real app lives in a
/// child window of class Windows.UI.Core.CoreWindow, so we hop to that
/// window's process before asking who it is. Without this, excluding any
/// Store app would silently do nothing.
public sealed class Win32WindowResolver : IWindowResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private const string FrameHost = "applicationframehost.exe";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    IntPtr IWindowResolver.GetForegroundWindow() => GetForegroundWindow();

    public AppIdentity? Resolve(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return null;
            if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return null;

            var exePath = ExePathOf(pid);
            if (exePath is null) return null;

            // Store app hosted in the frame host: hop to the real process.
            if (string.Equals(System.IO.Path.GetFileName(exePath), FrameHost, StringComparison.OrdinalIgnoreCase))
            {
                var inner = FindCoreWindowPid(hwnd, pid);
                if (inner is { } innerPid)
                {
                    var innerPath = ExePathOf(innerPid);
                    if (innerPath is not null) { pid = innerPid; exePath = innerPath; }
                }
            }

            var family = PackageFamilyOf(pid);
            if (family is not null)
                return new AppIdentity(AppKind.Package, family, family);

            return new AppIdentity(AppKind.Exe, exePath, FriendlyName(exePath), exePath);
        }
        catch (Exception ex)
        {
            Security.Log.Write($"Win32WindowResolver: resolve failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? ExePathOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;   // typically an elevated process
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static string? PackageFamilyOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            var rc = GetPackageFamilyName(h, ref len, null);
            if (rc == APPMODEL_ERROR_NO_PACKAGE || len == 0) return null;

            var sb = new StringBuilder((int)len);
            rc = GetPackageFamilyName(h, ref len, sb);
            return rc == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// The PID of the CoreWindow child, when it differs from the frame host.
    private static uint? FindCoreWindowPid(IntPtr parent, uint hostPid)
    {
        uint found = 0;
        EnumChildWindows(parent, (child, _) =>
        {
            var cls = new StringBuilder(256);
            if (GetClassName(child, cls, cls.Capacity) == 0) return true;
            if (!string.Equals(cls.ToString(), CoreWindowClass, StringComparison.Ordinal)) return true;

            if (GetWindowThreadProcessId(child, out var childPid) != 0 && childPid != hostPid)
            {
                found = childPid;
                return false;   // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        return found == 0 ? null : found;
    }

    /// Prefer the executable's own description ("KeePassXC") over the raw
    /// file name; fall back to the file name when it has none.
    private static string FriendlyName(string exePath)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc!;
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(exePath);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
```

- [ ] **Step 3: Implement `ForegroundTracker`**

Create `clipsync-win/ClipSync.Core/Clipboard/ForegroundTracker.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using ClipSync.Settings;

namespace ClipSync.Clipboard;

/// Watches foreground changes and remembers a short history of them.
///
/// Uses SetWinEventHook rather than polling: the OS calls us only when
/// focus actually moves. Must be started on the UI thread, because
/// WINEVENT_OUTOFCONTEXT callbacks are delivered on the message loop of
/// the thread that registered the hook, and UnhookWinEvent must run on
/// that same thread.
public sealed class ForegroundTracker
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly ForegroundRing _ring = new();
    private readonly IWindowResolver _resolver;

    private IntPtr _hook;
    // Held in a field on purpose: the delegate is passed to unmanaged
    // code, and if it is collected the next callback tears down the app.
    private WinEventProc? _callback;

    public ForegroundTracker() : this(new Win32WindowResolver()) { }

    public ForegroundTracker(IWindowResolver resolver) => _resolver = resolver;

    public void Start()
    {
        // Seed with whatever is in front now, so the first copy after
        // launch resolves without waiting for a focus change.
        try { _ring.Record(DateTime.UtcNow, _resolver.Resolve(_resolver.GetForegroundWindow())); }
        catch (Exception ex) { Security.Log.Write($"ForegroundTracker: seed failed: {ex.GetType().Name}"); }

        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                IntPtr.Zero, _callback, 0, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
            Security.Log.Write("ForegroundTracker: SetWinEventHook failed; app exclusions will not be enforced");
        else
            Security.Log.Write("ForegroundTracker: started");
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _callback = null;
    }

    public AppIdentity? AppAt(DateTime utc) => _ring.AppAt(utc);

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd,
                                     int idObject, int idChild, uint thread, uint time)
    {
        // Record the timestamp before resolving: resolution can take a
        // moment and the transition happened now, not when we finished.
        var at = DateTime.UtcNow;
        AppIdentity? app = null;
        try { app = _resolver.Resolve(hwnd); }
        catch (Exception ex) { Security.Log.Write($"ForegroundTracker: resolve failed: {ex.GetType().Name}"); }
        _ring.Record(at, app);
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd,
                                       int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
                                                 WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
```

- [ ] **Step 4: Verify it builds with no new warnings**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Core\ClipSync.Core.csproj -c Debug -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 5: Run the whole suite (nothing should regress)**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj
```

Expected: PASS, 25 tests.

- [ ] **Step 6: Commit**

```bash
git add clipsync-win/ClipSync.Core
git commit -m "Add ForegroundTracker with Win32 foreground hook

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Suppress transmission in `ClipboardWatcher`

This task makes the feature functionally complete without any UI. After it, an exclusion added by hand-editing `settings.json` works.

**Files:**
- Modify: `clipsync-win/ClipSync/Clipboard/ClipboardWatcher.cs`
- Modify: `clipsync-win/ClipSync/App.xaml.cs`

**Interfaces:**
- Consumes: `AppSettings`, `ForegroundTracker`, `Security.Log` from Tasks 2–4.
- Produces: `ClipboardWatcher(ClipboardWriter writer, ForegroundTracker foreground, AppSettings settings)`; `App.Settings` and `App.Foreground` properties used by Tasks 7–8.

- [ ] **Step 1: Change the watcher's constructor and fields**

In `clipsync-win/ClipSync/Clipboard/ClipboardWatcher.cs`, add `using ClipSync.Settings;` to the usings, then replace:

```csharp
    private readonly ClipboardWriter _writer;
    private ulong _seq = 1;

    public ClipboardWatcher(ClipboardWriter writer) { _writer = writer; }
```

with:

```csharp
    private readonly ClipboardWriter _writer;
    private readonly ForegroundTracker _foreground;
    private readonly AppSettings _settings;
    private ulong _seq = 1;

    public ClipboardWatcher(ClipboardWriter writer, ForegroundTracker foreground, AppSettings settings)
    {
        _writer = writer;
        _foreground = foreground;
        _settings = settings;
    }
```

- [ ] **Step 2: Capture the copy timestamp at the earliest possible point**

Replace:

```csharp
    public void Start()
    {
        WinClipboard.ContentChanged += async (_, _) => await OnChangedAsync();
    }

    private async Task OnChangedAsync()
    {
```

with:

```csharp
    public void Start()
    {
        // Timestamp here, synchronously: OnChangedAsync awaits several
        // WinRT calls, so by the time it has an item the user may have
        // switched apps. This is the only trustworthy anchor to the copy.
        WinClipboard.ContentChanged += async (_, _) =>
        {
            var copiedAt = DateTime.UtcNow;
            await OnChangedAsync(copiedAt);
        };
    }

    private async Task OnChangedAsync(DateTime copiedAt)
    {
```

- [ ] **Step 3: Add the exclusion check**

Replace:

```csharp
            if (_writer.ConsumeRecentWrite(item.CanonicalHash())) return;
            OnLocalCopy?.Invoke(item);
```

with:

```csharp
            // Loop suppression stays first: if the exclusion check
            // short-circuited it, the recent-write marker would survive
            // into the next copy and cause a spurious echo.
            if (_writer.ConsumeRecentWrite(item.CanonicalHash())) return;

            // Suppress transmission only — the item is already in the
            // local clipboard and Win+V, and we deliberately leave it
            // there. An unresolved source app falls open and is sent.
            var source = _foreground.AppAt(copiedAt);
            if (source is not null && _settings.IsExcluded(source))
            {
                Identity.Log($"ClipboardWatcher: suppressed item from {source.DisplayName} " +
                             $"({item.Formats.Count} formats)");
                return;
            }

            OnLocalCopy?.Invoke(item);
```

- [ ] **Step 4: Wire it up in `App.xaml.cs`**

Add `using ClipSync.Settings;` to the usings. Then add these two properties beside the existing ones:

```csharp
    public AppSettings Settings { get; private set; } = null!;
    public ForegroundTracker Foreground { get; private set; } = null!;
```

In `OnLaunched`, replace:

```csharp
            Identity = Identity.LoadOrCreate();
            TrustStore = TrustStore.Load();
            Peers = new PeerRegistry(Identity.DidHex);
            Writer = new ClipboardWriter();
            Watcher = new ClipboardWatcher(Writer);
```

with:

```csharp
            Identity = Identity.LoadOrCreate();
            // Route Core's diagnostics into the same opt-in debug log.
            ClipSync.Security.Log.Sink = Identity.Log;
            TrustStore = TrustStore.Load();
            Settings = AppSettings.Load();
            Peers = new PeerRegistry(Identity.DidHex);
            Writer = new ClipboardWriter();
            Foreground = new ForegroundTracker();
            Watcher = new ClipboardWatcher(Writer, Foreground, Settings);
```

and replace:

```csharp
            Watcher.Start();
            Discovery.Start();
```

with:

```csharp
            // Before the watcher, so the seed entry predates any copy.
            Foreground.Start();
            Watcher.Start();
            Discovery.Start();
```

- [ ] **Step 5: Build and verify**

(`Identity.Log` is `internal` and `App` is in the same assembly, so the
`Log.Sink = Identity.Log` method-group assignment compiles as written.)

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync\ClipSync.csproj -c Debug -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` with the 6 pre-existing warnings and no new ones.

- [ ] **Step 6: Smoke-test the headless feature**

```powershell
# Enable logging and exclude Notepad by hand.
$dir = "$env:LOCALAPPDATA\ClipSync"
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content -Encoding utf8 "$dir\settings.json" @'
{ "version": 1, "excludedApps": [ { "kind": "exe", "key": "notepad.exe", "name": "Notepad" } ] }
'@
& "C:\Users\perry\src\clipsync\clipsync-win\ClipSync\bin\x64\Debug\net10.0-windows10.0.26100.0\ClipSync.exe" --debug
```

Open Notepad, type something, copy it. Then:

```powershell
Get-Content "$env:LOCALAPPDATA\ClipSync\debug.log" -Tail 20
```

Expected: a line `ClipboardWatcher: suppressed item from Notepad (N formats)`. Copy from a different app and confirm no suppression line appears for it. Quit the app from the tray when done.

- [ ] **Step 7: Commit**

```bash
git add clipsync-win/ClipSync/Clipboard/ClipboardWatcher.cs clipsync-win/ClipSync/App.xaml.cs
git commit -m "Suppress transmission of items copied in excluded apps

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Enumerate installed apps

**Files:**
- Create: `clipsync-win/ClipSync/UI/InstalledApps.cs`

**Interfaces:**
- Consumes: `AppIdentity`, `AppKind`.
- Produces: `ClipSync.UI.InstalledApp` — `sealed record InstalledApp(AppIdentity Identity, Microsoft.UI.Xaml.Media.ImageSource? Icon)`; `ClipSync.UI.InstalledApps` with `static IReadOnlyList<InstalledApp> Enumerate()` (call off the UI thread) and `static AppIdentity FromExecutable(string exePath)`.

This lives in the app project, not Core: it produces WinUI `ImageSource` values and is verified manually rather than by unit test.

- [ ] **Step 1: Implement the enumerator**

Create `clipsync-win/ClipSync/UI/InstalledApps.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ClipSync.Settings;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClipSync.UI;

public sealed record InstalledApp(AppIdentity Identity, ImageSource? Icon);

/// Enumerates the shell's AppsFolder — the same list Start > All apps
/// shows, covering both desktop and Store apps.
///
/// Each child's parent-relative parsing name is its AUMID. Store apps use
/// `PackageFamilyName!AppId`; desktop entries are backed by a Start Menu
/// shortcut whose target executable we read from PKEY_Link_TargetParsing.
public static class InstalledApps
{
    /// Blocking: enumerating and rasterising a few hundred icons takes
    /// roughly 200-500 ms. Call from a background thread.
    public static IReadOnlyList<InstalledApp> Enumerate()
    {
        var results = new List<InstalledApp>();
        var seen = new HashSet<AppIdentity>();

        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folder) != 0
                || folder is null)
            {
                Security.Identity.Log("InstalledApps: could not open AppsFolder");
                return results;
            }

            var enumIid = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, BHID_EnumItems, ref enumIid, out var enumObj);
            if (enumObj is not IEnumShellItems items)
            {
                Security.Identity.Log("InstalledApps: AppsFolder returned no enumerator");
                return results;
            }

            var buffer = new IShellItem[1];
            while (items.Next(1, buffer, out var fetched) == 0 && fetched == 1)
            {
                var item = buffer[0];
                try
                {
                    var identity = IdentityOf(item);
                    if (identity is null || !seen.Add(identity)) continue;
                    results.Add(new InstalledApp(identity, IconOf(item)));
                }
                catch (Exception ex)
                {
                    Security.Identity.Log($"InstalledApps: skipped an entry: {ex.GetType().Name}");
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"InstalledApps: enumeration failed: {ex.GetType().Name}");
        }

        results.Sort((a, b) => string.Compare(a.Identity.DisplayName, b.Identity.DisplayName,
                                              StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    /// Build an Exe identity from a path the user browsed to.
    public static AppIdentity FromExecutable(string exePath)
    {
        string name;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            name = string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(exePath) : desc!;
        }
        catch { name = Path.GetFileNameWithoutExtension(exePath); }

        return new AppIdentity(AppKind.Exe, exePath, name, exePath);
    }

    private static AppIdentity? IdentityOf(IShellItem item)
    {
        item.GetDisplayName(SIGDN.NORMALDISPLAY, out var displayPtr);
        var display = Marshal.PtrToStringUni(displayPtr) ?? "";
        Marshal.FreeCoTaskMem(displayPtr);

        item.GetDisplayName(SIGDN.PARENTRELATIVEPARSING, out var aumidPtr);
        var aumid = Marshal.PtrToStringUni(aumidPtr) ?? "";
        Marshal.FreeCoTaskMem(aumidPtr);

        if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(aumid)) return null;

        // Store app: PackageFamilyName!AppId
        var bang = aumid.IndexOf('!');
        if (bang > 0) return new AppIdentity(AppKind.Package, aumid[..bang], display);

        // Desktop app: resolve the backing shortcut's target executable.
        var target = TargetOf(item);
        if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new AppIdentity(AppKind.Exe, target, display, target);
    }

    private static string? TargetOf(IShellItem item)
    {
        if (item is not IShellItem2 item2) return null;
        try
        {
            var key = PKEY_Link_TargetParsing;
            return item2.GetString(ref key, out var value) == 0 ? value : null;
        }
        catch { return null; }
    }

    private static ImageSource? IconOf(IShellItem item)
    {
        if (item is not IShellItemImageFactory factory) return null;
        var hbitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = 32, cy = 32 }, SIIGBF.ICONONLY, out hbitmap) != 0
                || hbitmap == IntPtr.Zero)
                return null;

            using var bmp = System.Drawing.Image.FromHbitmap(hbitmap);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return FromPngBytes(ms.ToArray());
        }
        catch { return null; }
        finally { if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap); }
    }

    /// Icons for browsed executables, which have no shell item.
    public static ImageSource? IconForExecutable(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return FromPngBytes(ms.ToArray());
        }
        catch { return null; }
    }

    private static ImageSource FromPngBytes(byte[] png)
    {
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        }
        var image = new BitmapImage();
        image.SetSource(stream);
        return image;
    }

    // ---- COM interop ----

    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    private static PROPERTYKEY PKEY_Link_TargetParsing => new()
    {
        fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"),
        pid = 2,
    };

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
    }

    [Flags]
    private enum SIIGBF { RESIZETOFIT = 0x00, ICONONLY = 0x04 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                           ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2 : IShellItem
    {
        new void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                               ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new void GetParent(out IShellItem parent);
        new void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        new void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        new void Compare(IShellItem psi, uint hint, out int order);

        void GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreWithCreateObject(uint flags, [MarshalAs(UnmanagedType.IUnknown)] object punk,
                                              ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, uint flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void Update(IntPtr pbc);
        void GetProperty(ref PROPERTYKEY key, out IntPtr ppropvar);
        void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        void GetFileTime(ref PROPERTYKEY key, out long pft);
        void GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt,
                               out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
```

- [ ] **Step 2: Build**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync\ClipSync.csproj -c Debug -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` with only the 6 pre-existing warnings.

- [ ] **Step 3: Verify enumeration returns real apps**

Temporarily add this to the top of `App.OnLaunched`'s `try` block, immediately after `Identity = Identity.LoadOrCreate();` and `ClipSync.Security.Log.Sink = Identity.Log;`:

```csharp
            System.Threading.Tasks.Task.Run(() =>
            {
                var apps = ClipSync.UI.InstalledApps.Enumerate();
                Identity.Log($"InstalledApps: enumerated {apps.Count}");
                foreach (var a in apps) Identity.Log($"  {a.Identity.Kind} {a.Identity.Key} = {a.Identity.DisplayName}");
            });
```

Rebuild, run with `--debug`, then:

```powershell
Select-String -Path "$env:LOCALAPPDATA\ClipSync\debug.log" -Pattern "InstalledApps:" | Select-Object -Last 30
```

Expected: a count in the dozens-to-hundreds, a mixture of `Exe` and `Package` kinds, and recognisable display names. **Remove the temporary block before committing.**

- [ ] **Step 4: Commit**

```bash
git add clipsync-win/ClipSync/UI/InstalledApps.cs
git commit -m "Add installed-app enumeration via shell AppsFolder

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: Settings window and tray entry point

**Files:**
- Create: `clipsync-win/ClipSync/UI/SettingsWindow.xaml`
- Create: `clipsync-win/ClipSync/UI/SettingsWindow.xaml.cs`
- Modify: `clipsync-win/ClipSync/UI/TrayPopup.xaml`
- Modify: `clipsync-win/ClipSync/UI/TrayPopup.xaml.cs`

**Interfaces:**
- Consumes: `App.Settings`, `AppIdentity`, `InstalledApps.IconForExecutable`.
- Produces: `ClipSync.UI.SettingsWindow` with `static void ShowSingleton()` and `void RefreshList()` (called by Task 8 after a successful add).

- [ ] **Step 1: Create the window markup**

Create `clipsync-win/ClipSync/UI/SettingsWindow.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="ClipSync.UI.SettingsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="ClipSync Settings">
    <Grid Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" Padding="20" RowSpacing="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Excluded apps" FontSize="20" FontWeight="SemiBold"/>

        <TextBlock Grid.Row="1" TextWrapping="Wrap"
                   Foreground="{ThemeResource SystemControlForegroundBaseMediumBrush}"
                   Text="Items copied while these apps are in the foreground are not sent to your other devices."/>

        <Grid Grid.Row="2">
            <ListView x:Name="ExcludedList" SelectionMode="None"
                      Background="{ThemeResource LayerFillColorDefaultBrush}"
                      BorderBrush="{ThemeResource SystemControlForegroundBaseMediumLowBrush}"
                      BorderThickness="1" CornerRadius="4"/>
            <TextBlock x:Name="EmptyText"
                       Text="No apps excluded. Everything you copy is synced."
                       Foreground="{ThemeResource SystemControlForegroundBaseMediumBrush}"
                       HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>

        <Button Grid.Row="3" x:Name="AddButton" Content="Add app…" Click="OnAddApp"
                HorizontalAlignment="Left"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Implement the window**

Create `clipsync-win/ClipSync/UI/SettingsWindow.xaml.cs`. `OnAddApp` is a stub until Task 8:

```csharp
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClipSync.Settings;

namespace ClipSync.UI;

public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _instance = null;
        RefreshList();
    }

    /// One settings window at a time; re-activate the existing one.
    public static void ShowSingleton()
    {
        _instance ??= new SettingsWindow();
        _instance.Activate();
    }

    public void RefreshList()
    {
        var excluded = App.Current.Settings.Excluded;
        ExcludedList.Items.Clear();
        EmptyText.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var app in excluded)
            ExcludedList.Items.Add(BuildRow(app));
    }

    private UIElement BuildRow(AppIdentity app)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        if (app.Path is { } p) icon.Source = InstalledApps.IconForExecutable(p);
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.DisplayName, FontSize = 14 });
        text.Children.Add(new TextBlock
        {
            Text = app.Path ?? app.Key,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        remove.Click += (_, _) =>
        {
            App.Current.Settings.Remove(app);
            Security.Identity.Log($"Settings: removed exclusion {app.DisplayName}");
            RefreshList();
        };
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        // Replaced in Task 8 by the app picker.
    }
}
```

- [ ] **Step 3: Add the tray entry point**

In `clipsync-win/ClipSync/UI/TrayPopup.xaml`, replace:

```xml
            <Button Content="Quit" Click="OnQuit" HorizontalAlignment="Stretch"/>
```

with:

```xml
            <Button Content="Settings…" Click="OnSettings" HorizontalAlignment="Stretch"/>
            <Button Content="Quit" Click="OnQuit" HorizontalAlignment="Stretch"/>
```

In `clipsync-win/ClipSync/UI/TrayPopup.xaml.cs`, add this method next to `OnQuit`:

```csharp
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        // Hide first: this popup dismisses itself on deactivation, so the
        // settings window stealing focus would close it anyway.
        Hide();
        SettingsWindow.ShowSingleton();
    }
```

- [ ] **Step 4: Fix the popup's height calculation**

`ShowAtCursor` sizes the popup from a hardcoded content height that now needs a second button. In `TrayPopup.xaml.cs`, replace:

```csharp
        int contentHeight = (int)((24 + 18 + 1 + (rows * 28) + 1 + 36 + 60) * scale);
```

with:

```csharp
        // header + DID + separator + peers + separator + 2 buttons + padding
        int contentHeight = (int)((24 + 18 + 1 + (rows * 28) + 1 + 36 + 36 + 8 + 60) * scale);
```

- [ ] **Step 5: Build**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync\ClipSync.csproj -c Debug -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` with only the 6 pre-existing warnings.

- [ ] **Step 6: Verify by hand**

Run the app, click the tray icon, click `Settings…`. Expected: a window listing the Notepad exclusion left over from Task 5's smoke test, with a working Remove button; after removing, the empty-state text appears and `settings.json` shows an empty `excludedApps`.

- [ ] **Step 7: Commit**

```bash
git add clipsync-win/ClipSync/UI
git commit -m "Add settings window listing excluded apps

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: App picker dialog

**Files:**
- Create: `clipsync-win/ClipSync/UI/AppPickerDialog.xaml`
- Create: `clipsync-win/ClipSync/UI/AppPickerDialog.xaml.cs`
- Modify: `clipsync-win/ClipSync/UI/SettingsWindow.xaml.cs` (`OnAddApp`)

**Interfaces:**
- Consumes: `InstalledApps.Enumerate()`, `InstalledApps.FromExecutable`, `App.Settings`.
- Produces: `ClipSync.UI.AppPickerDialog` with `static Task<AppIdentity?> PickAsync(XamlRoot root)`.

- [ ] **Step 1: Create the dialog markup**

Create `clipsync-win/ClipSync/UI/AppPickerDialog.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ContentDialog
    x:Class="ClipSync.UI.AppPickerDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="Add an app to exclude"
    PrimaryButtonText="Add"
    SecondaryButtonText="Browse…"
    CloseButtonText="Cancel"
    DefaultButton="Primary">
    <Grid RowSpacing="8" Height="420" Width="420">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBox Grid.Row="0" x:Name="SearchBox" PlaceholderText="Search apps"
                 TextChanged="OnSearchChanged"/>

        <Grid Grid.Row="1">
            <ListView x:Name="AppList" SelectionMode="Single"/>
            <ProgressRing x:Name="Busy" IsActive="True"
                          HorizontalAlignment="Center" VerticalAlignment="Center"/>
            <TextBlock x:Name="ErrorText" Visibility="Collapsed" TextWrapping="Wrap"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       Foreground="{ThemeResource SystemControlForegroundBaseMediumBrush}"
                       Text="Could not read the installed-app list. Use Browse… to pick an .exe instead."/>
        </Grid>
    </Grid>
</ContentDialog>
```

- [ ] **Step 2: Implement the dialog**

Create `clipsync-win/ClipSync/UI/AppPickerDialog.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ClipSync.Settings;
using WinRT.Interop;

namespace ClipSync.UI;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly Window _owner;
    private IReadOnlyList<InstalledApp> _all = Array.Empty<InstalledApp>();
    private AppIdentity? _browsed;
    private bool _enumerationFailed;

    /// `owner` supplies the HWND that FileOpenPicker must parent itself to.
    public AppPickerDialog(Window owner)
    {
        InitializeComponent();
        _owner = owner;
        SecondaryButtonClick += OnBrowse;
        Loaded += OnLoaded;
    }

    /// Shows the picker; returns the chosen app, or null if cancelled.
    public static async Task<AppIdentity?> PickAsync(XamlRoot root, Window owner)
    {
        var dialog = new AppPickerDialog(owner) { XamlRoot = root };
        var result = await dialog.ShowAsync();

        // Browse dismisses via Hide(), which yields None rather than
        // Primary, so a browsed pick has to be checked either way.
        if (dialog._browsed is { } browsed) return browsed;
        if (result == ContentDialogResult.Primary
            && dialog.AppList.SelectedItem is ListViewItem { Tag: AppIdentity picked })
            return picked;
        return null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Enumeration plus icon rasterising is too slow for the UI thread.
        var apps = await Task.Run(InstalledApps.Enumerate);

        // An empty result means enumeration failed; a list that is empty
        // only after filtering just means everything is already excluded.
        _enumerationFailed = apps.Count == 0;

        var alreadyExcluded = App.Current.Settings.Excluded.ToHashSet();
        _all = apps.Where(a => !alreadyExcluded.Contains(a.Identity)).ToList();

        Busy.IsActive = false;
        Busy.Visibility = Visibility.Collapsed;
        if (_enumerationFailed) ErrorText.Visibility = Visibility.Visible;

        Populate(_all);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        Populate(string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(a => a.Identity.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                  .ToList());
    }

    private void Populate(IReadOnlyList<InstalledApp> apps)
    {
        AppList.Items.Clear();
        foreach (var app in apps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Image
            {
                Width = 24, Height = 24, Source = app.Icon,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = app.Identity.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
            });
            AppList.Items.Add(new ListViewItem { Content = row, Tag = app.Identity });
        }
    }

    /// SecondaryButton = Browse. Keep the dialog open while the file
    /// picker runs, then close it as if Add had been pressed.
    private async void OnBrowse(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                _browsed = InstalledApps.FromExecutable(file.Path);
                Hide();
            }
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"AppPicker: browse failed: {ex.GetType().Name}");
        }
        finally { deferral.Complete(); }
    }
}
```

- [ ] **Step 3: Call the picker from the settings window**

In `clipsync-win/ClipSync/UI/SettingsWindow.xaml.cs`, replace `OnAddApp` with:

```csharp
    private async void OnAddApp(object sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await AppPickerDialog.PickAsync(Content.XamlRoot, this);
            if (picked is null) return;

            App.Current.Settings.Add(picked);
            Security.Identity.Log($"Settings: added exclusion {picked.DisplayName}");
            RefreshList();
        }
        catch (Exception ex)
        {
            Security.Identity.Log($"Settings: add failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
```

- [ ] **Step 4: Build**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync\ClipSync.csproj -c Debug -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` with only the 6 pre-existing warnings.

- [ ] **Step 5: Verify by hand**

Run the app → tray → `Settings…` → `Add app…`. Expected: a progress ring briefly, then a searchable list of installed apps with icons. Type to filter. Select one, click `Add` — it appears in the settings list and in `settings.json`. Re-open the picker and confirm the app you just added is no longer offered. Click `Browse…` and pick any `.exe` — it is added too.

- [ ] **Step 6: Commit**

```bash
git add clipsync-win/ClipSync/UI
git commit -m "Add installed-app picker dialog

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 9: End-to-end verification and documentation

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Run the full test suite**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet test C:\Users\perry\src\clipsync\clipsync-win\ClipSync.Tests\ClipSync.Tests.csproj
```

Expected: PASS, 25 tests.

- [ ] **Step 2: Build Release and confirm no new warnings**

```powershell
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
& $dotnet build C:\Users\perry\src\clipsync\clipsync-win\ClipSync\ClipSync.csproj -c Release -p:Platform=x64 --nologo
```

Expected: `Build succeeded.` 6 warnings, 0 errors.

- [ ] **Step 3: Work through the manual matrix**

Run with `--debug`, with a peer (kodachrome) awake on the LAN and connected. **A sleeping Mac looks identical to a discovery failure — confirm `ping 10.0.0.156` first.**

| # | Action | Expected |
|---|---|---|
| 1 | Exclude a Win32 app (e.g. Notepad); copy in it | Nothing arrives on the peer; log shows `suppressed item from …` |
| 2 | Copy in a non-excluded app | Item arrives on the peer as normal |
| 3 | Exclude a Store app (Windows Terminal); copy in it | Suppressed — proves the `ApplicationFrameHost` hop works |
| 4 | Copy in an excluded app, then Alt-Tab immediately | Still suppressed |
| 5 | Remove the exclusion; copy again | Item syncs, no restart needed |
| 6 | Restart ClipSync; open Settings | Exclusions persisted |
| 7 | Copy in an excluded app; press Win+V | The item **is** in local clipboard history — suppression is transmit-only |
| 8 | Delete `settings.json`; start the app | Starts clean with an empty list, no crash |

- [ ] **Step 4: Update the README**

In `README.md`, add to the end of the feature description (after the first paragraph):

```markdown
Apps can be excluded from sync: open the tray menu → **Settings…** →
**Add app…** and pick from the installed-app list. Anything copied while
an excluded app is in the foreground stays local — it is still placed in
your own clipboard and Win+V history, but never sent to a peer. Apps
running elevated cannot be excluded, because a non-elevated ClipSync
cannot identify them.
```

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "Document excluded apps in README

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Notes for the implementer

- **The `ForegroundTracker` delegate field is load-bearing.** `_callback` must stay referenced for the lifetime of the hook. If it is inlined into the `SetWinEventHook` call, the GC collects it and the next foreground change crashes the process.
- **`Foreground.Start()` must run on the UI thread**, and before `Watcher.Start()`.
- **Do not move the exclusion check ahead of `ConsumeRecentWrite`.** The comment in the code explains why; the failure mode is a spurious echo, which is hard to diagnose after the fact.
- The `Stop()` method exists for symmetry and for a future clean-shutdown path. `Application.Current.Exit()` currently tears the process down without calling it, which is acceptable — the OS releases the hook.
- **`System.Drawing` is used as the HBITMAP/HICON → PNG bridge** in Task 6, because WinUI 3 has no direct equivalent. `TrayIcon.cs` already uses `System.Drawing.Icon`, so the types resolve today via the `H.NotifyIcon.WinUI` dependency chain. If Task 6 fails to compile with a missing-type error, adding `<PackageReference Include="System.Drawing.Common" Version="9.0.5" />` to `ClipSync.csproj` is the sanctioned exception to the no-new-dependencies constraint.
- `TransferLog` is dead code on both platforms, so `transfers.log` will not distinguish suppressed from sent items. Use `debug.log` for verification.
