# Excluded apps — design

Date: 2026-08-09
Status: approved, ready for implementation planning

## Summary

Let the user nominate applications whose clipboard activity ClipSync must
not transmit. When the user copies while an excluded app is in the
foreground, the item still lands in the local clipboard and Win+V history
exactly as today, but it is never broadcast to peers.

Apps are chosen from a picker listing installed applications, with a
`Browse…` escape hatch for anything the list misses.

This spec covers both platforms so the settings format and the matching
semantics are agreed cross-platform. **Only the Windows half is
implemented in this pass**; macOS is a follow-up that reuses this spec
without redesign.

## Goals

- Suppress *transmission* of clipboard items copied while an excluded app
  is frontmost.
- Choose apps from a list of installed applications, not by typing a path.
- Persist the exclusion list across restarts.
- Changes take effect immediately, without restarting the app.

## Non-goals

- Excluding *inbound* items. Exclusion only affects what this device sends.
- Syncing the exclusion list between peers. It is per-device.
- Per-format or per-peer exclusion rules.
- Blocking the local clipboard or Win+V history. ClipSync does not touch
  the clipboard on the outbound path and will not start.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Platform scope | Design both, implement Windows | Only Windows is buildable/testable on this machine |
| Source-app detection | Cached foreground tracker (`SetWinEventHook`) | Matches the stated semantics; avoids the async race and the unreliability of `GetClipboardOwner` |
| App picker source | `shell:AppsFolder` + `Browse…` | Exactly the list Start > All apps shows; covers Win32 and Store apps |
| Unknown source app | Fail open (transmit) | Silent non-delivery is a worse failure than a rare miss |
| Win32 matching key | Executable **file name**, case-insensitive | Path matching breaks silently on versioned-directory auto-updates |
| Settings storage | Plain JSON in `%LOCALAPPDATA%\ClipSync\` | A preference, not a secret; hand-editable |

### Rejected alternatives

**Live foreground query at event time.** `GetForegroundWindow()` called
from the `ContentChanged` handler. Rejected: `OnChangedAsync` awaits
several WinRT calls before it has the item, so copying and immediately
Alt-Tabbing reads the app switched *to*. The miss fails in the leaking
direction and is silent.

**`GetClipboardOwner()`.** Semantically the truest signal — the window
that actually called `SetClipboardData`. Rejected: unreliable in
practice. Delayed rendering and hidden helper windows report a foreign
process, WinRT `DataPackage` writes go through a broker, clipboard
history can re-own the data, and it returns NULL often enough that the
feature would quietly under-enforce.

**Matching on full executable path.** Looks more precise. Rejected:
Discord, Slack, Teams and VS Code Insiders install into versioned
directories, so each auto-update would silently un-exclude the app.

## Architecture

### New Windows components

| File | Responsibility | Interface |
|---|---|---|
| `Settings/AppIdentity.cs` | Value type naming an app | `record AppIdentity(AppKind Kind, string Key, string DisplayName, string? Path)`; `AppKind ∈ { Exe, Package }` |
| `Settings/AppSettings.cs` | Load/save user preferences | `Load()`, `IsExcluded(AppIdentity)`, `Add(AppIdentity)`, `Remove(AppIdentity)`, `IReadOnlyList<AppIdentity> Excluded` |
| `Clipboard/ForegroundTracker.cs` | Know which app was frontmost, and when | `Start()`, `Stop()`, `AppIdentity? AppAt(DateTime utc)` |
| `UI/InstalledApps.cs` | Enumerate the Start-menu app list | `IReadOnlyList<InstalledApp> Enumerate()` |
| `UI/SettingsWindow.xaml(.cs)` | Show and edit the exclusion list | Window, opened from the tray popup |
| `UI/AppPickerDialog.xaml(.cs)` | Search and pick an installed app | `ContentDialog`, returns `AppIdentity?` |

### Modified

- `Clipboard/ClipboardWatcher.cs` — takes `ForegroundTracker` and
  `AppSettings`; adds the exclusion check.
- `App.xaml.cs` — constructs and starts `AppSettings` and
  `ForegroundTracker`; stops the tracker on shutdown.
- `UI/TrayPopup.xaml(.cs)` — adds a `Settings…` button above `Quit`.

### AppIdentity

Equality is on `Kind` + `Key` only. `DisplayName` and `Path` are
presentation data; a renamed or relocated app still matches.

- `Kind = Exe` — `Key` is the executable file name, lowercased
  (`keepassxc.exe`). `Path` holds the full path the user picked, shown in
  the UI so an ambiguous name is visible.
- `Kind = Package` — `Key` is the package family name
  (`Microsoft.WindowsTerminal_8wekyb3d8bbwe`), compared ordinal
  case-insensitive. `Path` is null.

### ForegroundTracker

Registers `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)`
on the UI thread — event-driven, no polling. Each transition appends
`(utcNow, AppIdentity?)` to a ring bounded two ways: entries older than
2 minutes are evicted, and the ring never holds more than 16 entries
(oldest dropped first). `Start()` seeds the ring from
`GetForegroundWindow()` so the first copy after launch resolves.

Each entry owns the **half-open interval** `[its timestamp, the next
entry's timestamp)`; the newest entry runs to infinity. `AppAt(t)`
returns the identity of the entry whose interval contains `t`, or null
if `t` predates the oldest retained entry. A `t` falling exactly on a
transition therefore resolves to the app that just became foreground,
not the one it replaced.

Resolving an HWND to an `AppIdentity`:

1. `GetWindowThreadProcessId` → PID.
2. If the process image is `ApplicationFrameHost.exe`, enumerate child
   windows for class `Windows.UI.Core.CoreWindow` and take that window's
   PID instead. **Store apps do not own their own foreground window**;
   without this, excluding any Store app silently does nothing.
3. `GetPackageFamilyName(hProcess)` — on success, `Kind = Package`. On
   `APPMODEL_ERROR_NO_PACKAGE`, `Kind = Exe` with
   `QueryFullProcessImageName`'s file name.
4. Any failure yields null, which falls open.

The Win32 calls sit behind a small interface so the ring can be driven
synthetically in tests.

## Data flow

In `ClipboardWatcher.OnChangedAsync`:

1. **First statement, before any `await`:** `var copiedAt = DateTime.UtcNow`.
   Everything downstream is async, so this is the only trustworthy anchor
   to the moment of the copy.
2. Build formats — unchanged.
3. `if (_writer.ConsumeRecentWrite(item.CanonicalHash())) return;` —
   unchanged, and **deliberately still ahead of the exclusion check**. If
   exclusion short-circuited it, the loop-suppression marker would leak
   into the next copy and cause a spurious echo.
4. New:
   ```csharp
   var src = _foreground.AppAt(copiedAt);
   if (src is not null && _settings.IsExcluded(src))
   {
       Identity.Log($"ClipboardWatcher: suppressed item from {src.DisplayName} ({item.Formats.Count} formats)");
       return;
   }
   ```
5. `OnLocalCopy?.Invoke(item)` — unchanged.

`src is null` falls through to transmit. The log line carries the display
name and format count only, never clipboard content, per the existing
logging rule in `Identity.Log`.

## Settings storage

`%LOCALAPPDATA%\ClipSync\settings.json`, plain JSON. A parse failure
falls back to defaults rather than throwing, matching `TrustStore.Load()`.

```json
{
  "version": 1,
  "excludedApps": [
    { "kind": "exe",     "key": "keepassxc.exe",                           "name": "KeePassXC",       "path": "C:\\Program Files\\KeePassXC\\KeePassXC.exe" },
    { "kind": "package", "key": "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "name": "Windows Terminal" },
    { "kind": "bundle",  "key": "com.apple.Notes",                         "name": "Notes" }
  ]
}
```

One schema for both platforms; macOS adds `kind: "bundle"` keyed by
bundle identifier. Unknown `kind` values are ignored on load, so a
settings file written by a future version degrades rather than breaking.

The file reveals which apps the user treats as sensitive. It is left
unencrypted deliberately, for hand-editability, and sits in a per-user
ACL'd directory. Wrapping it in DPAPI like `trust.json.dpapi` is a
contained change if that trade-off is revisited.

## UI

### Entry point

A `Settings…` button in `TrayPopup`, above `Quit`. It opens
`SettingsWindow` and hides the popup.

`SettingsWindow` is a normal window, not a popup. `TrayPopup` hides on
deactivation (`TrayPopup.xaml.cs:34`), so a dialog opened from it would
dismiss its own parent.

### SettingsWindow

- Heading: "Excluded apps".
- Explainer: "Items copied while these apps are in the foreground are not
  sent to your other devices."
- `ListView` of excluded apps: icon, display name, and the full path or
  package family name on a secondary line, with a Remove button per row.
- Empty state: "No apps excluded. Everything you copy is synced."
- `Add app…` button at the bottom.

### AppPickerDialog

A `ContentDialog` hosted in `SettingsWindow`:

- Search box filtering the list as the user types.
- Virtualized `ListView` of installed apps — 32 px icon plus display name.
- `Browse…` button using `FileOpenPicker` filtered to `.exe`, producing an
  `Exe` identity from the chosen file.
- OK / Cancel.

Enumeration and icon extraction run on a background thread behind a
`ProgressRing`; a few hundred entries takes roughly 200–500 ms, too slow
for the UI thread. Results are cached for the lifetime of the dialog.

Apps already excluded are omitted from the list.

### InstalledApps

Enumerates the `shell:AppsFolder` known folder via
`IShellItem`/`IEnumShellItems`. Each entry's parsing name is its AUMID:

- Contains `!` → packaged app. The portion before `!` is the package
  family name → `Kind = Package`.
- Otherwise → Win32 app backed by a Start Menu shortcut. Read
  `PKEY_Link_TargetParsing` for the target executable → `Kind = Exe` with
  that file's name as the key and its full path for display.
- An entry that resolves to neither is skipped.

Icons come from `IShellItemImageFactory` at 32 px;
`ExtractAssociatedIcon` covers browsed executables.

## macOS (specified now, implemented later)

Substantially simpler than Windows.
`NSWorkspace.shared.frontmostApplication` yields an
`NSRunningApplication` with a `bundleIdentifier` directly — there is no
`ApplicationFrameHost` indirection and no second identity kind, so every
macOS entry is `kind: "bundle"`.

- **Tracker** — `Sources/ClipSync/Clipboard/ForegroundTracker.swift`
  observes `NSWorkspace.shared.notificationCenter` for
  `didActivateApplicationNotification` and maintains the same timestamped
  ring with the same half-open interval semantics.
- **Installed apps** — scan `/Applications`, `/System/Applications` and
  `~/Applications` for `.app` bundles, reading `CFBundleIdentifier`,
  `CFBundleName` and the bundle icon. `Browse…` uses `NSOpenPanel`
  restricted to application bundles.
- **Settings** — `~/Library/Application Support/ClipSync/settings.json`,
  the schema above.
- **UI** — a `Settings…` entry in `MenuBarView` opening a separate
  window. The menu-bar popover dismisses on deactivation, the same
  constraint that rules out hosting the picker in the Windows tray popup.

One macOS-only nuance follows from `PasteboardWatcher` polling
`changeCount` every 200 ms: the copy time is known only to within that
interval. The rule there is that **if any app held focus during the
interval preceding the tick and that app is excluded, the item is
suppressed**. This is deliberately stricter than the Windows path — the
alternative leaks on a fast copy-then-switch — and the window is small
enough that a surprising suppression should be vanishingly rare. It is a
divergence from the Windows behaviour and is intentional; it does not
need to be mirrored back to Windows, where the exact copy timestamp is
available.

## Error handling

Every failure path falls open and logs **once**, not per copy.

| Failure | Behaviour |
|---|---|
| `SetWinEventHook` fails | Tracker permanently returns null; everything transmits; one startup log line |
| Elevated app in foreground | `QueryFullProcessImageName` is denied to a non-elevated process → null → transmits. **A stated limitation: apps running elevated cannot be excluded.** |
| AppsFolder enumeration fails | Picker degrades to `Browse…` only, with an inline message |
| `settings.json` corrupt or unreadable | Empty exclusion list, logged |
| Icon extraction fails | Placeholder icon; entry still selectable |
| Settings write fails | Logged; in-memory list keeps working for the session |

The hook is unregistered on the thread that set it, during app shutdown.

## Testing

### Unit tests (new project)

The repo has no test project today. This adds one — `clipsync-win/ClipSync.Tests/`,
xunit — scoped to logic where a silent regression would be invisible:

- `AppIdentity` — equality on `Kind`+`Key` only; case-insensitive exe-name
  matching; display name and path do not affect equality.
- `AppSettings` — round-trip save/load; corrupt file yields an empty list;
  unknown `kind` entries are ignored; add/remove are idempotent.
- `ForegroundTracker` ring — `AppAt` before any transition returns null;
  inside an interval returns that interval's app; after eviction returns
  null; a timestamp falling exactly on a transition resolves to the
  newly-activated app, per the half-open interval rule.

The Win32 resolution path sits behind an interface so the ring is driven
synthetically without real windows.

### Manual verification

1. Exclude a Win32 app; copy from it; confirm nothing reaches the peer and
   the log shows the suppression.
2. Copy from a non-excluded app; confirm it still arrives.
3. Repeat (1) with a Store app (Windows Terminal) to prove the
   `ApplicationFrameHost` path works.
4. Copy in an excluded app, Alt-Tab immediately; confirm still suppressed
   (the race Approach A would have lost).
5. Remove the exclusion; confirm the app syncs again with no restart.
6. Restart the app; confirm exclusions persist.
7. Confirm excluded-app copies still land in the local clipboard and Win+V.

End-to-end steps need kodachrome awake on the LAN; a sleeping Mac looks
identical to a discovery failure.

## Open follow-ups (out of scope)

- macOS implementation, per the design above.
- `TransferLog` is currently dead code on both platforms, so
  `transfers.log` will not show suppressed-vs-sent counts until it is
  wired up. Unrelated to this feature, noted because it would otherwise
  be the obvious way to verify step 1 above.
