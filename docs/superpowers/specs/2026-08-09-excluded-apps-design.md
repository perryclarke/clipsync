# Send-side controls: excluded apps and pausing — design

Date: 2026-08-09; pausing added 2026-08-14
Status: implemented on Windows; macOS not started

## Summary

Two ways to stop this device sending, sharing one idea: **what leaves this
machine is the user's to control, and nothing about receiving changes.**

1. **Excluded apps.** Nominate applications whose clipboard activity
   ClipSync must not transmit. Copy while an excluded app is frontmost and
   the item still lands in the local clipboard and Win+V history exactly as
   today, but it is never broadcast. Apps are chosen from a picker listing
   installed applications, with a `Browse…` escape hatch for anything the
   list misses.
2. **Pausing.** Stop sending on demand — globally, or to one named peer.

They are independent and compose: an item goes out only if no gate is
closed. Neither affects the inbound path. Items from peers keep arriving
and keep reaching the local clipboard whatever is excluded or paused.

This spec covers both platforms so the settings format and the matching
semantics are agreed cross-platform. **Both features are implemented on
Windows**; macOS is a follow-up that reuses this spec without redesign.
See `docs/superpowers/plans/2026-08-14-excluded-apps-macos-handoff.md` for
what building the Windows half actually taught, including the parts of
this spec that turned out to be wrong.

## Goals

- Suppress *transmission* of clipboard items copied while an excluded app
  is frontmost.
- Choose apps from a list of installed applications, not by typing a path.
- Persist the exclusion list across restarts.
- Stop sending on demand, either to everybody or to one named peer.
- Make a paused device say so without the user having to go looking.
- Changes take effect immediately, without restarting the app.

## Non-goals

- Affecting *inbound* items. Everything here governs what this device
  sends. A device that is fully paused still receives.
- Queueing or replaying suppressed items. Nothing is held back for later;
  a resumed device sends the next thing copied, not the last one missed.
- Syncing any of this between peers. Both lists are per-device. A peer is
  not told it has been paused.
- Per-format exclusion rules, or excluding an app for one peer only. The
  two axes do not cross: apps are excluded from everything, peers are
  paused for everything.
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
| Pause scope | Send only, both globally and per peer | Same verb for both controls; a paused device that stopped receiving would be a different feature |
| Global pause persistence | **Not** persisted | It means "not right now"; a restart that silently left syncing off is a bad surprise |
| Per-peer pause persistence | Persisted | A lasting preference about that machine, like muting a contact |
| Pause gate location | One predicate consulted per peer in the broadcast loop | Both cases arrive through one place; the peer registry never learns what a pause is |

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
  ],
  "pausedPeers": [
    "b6bf89d94fc27ef9a4b7d0da5f8fae81342bb27e542ed64c545b1401744b1e83"
  ]
}
```

One schema for both platforms; macOS adds `kind: "bundle"` keyed by
bundle identifier. Unknown `kind` values are ignored on load, so a
settings file written by a future version degrades rather than breaking.

`pausedPeers` holds peer DIDs in lowercase hex, and is the *only* pause
state on disk — a global pause is in-memory by design. Entries are
normalised to lowercase and de-duplicated on load; blank ones are
dropped. An entry naming a peer this device has never met is harmless
and is kept, so that pausing a machine, forgetting it, and meeting it
again does not silently un-pause it.

Both features share one file, so neither may clobber the other on write.
Windows has a test for exactly that.

The file reveals which apps the user treats as sensitive. It is left
unencrypted deliberately, for hand-editability, and sits in a per-user
ACL'd directory. Wrapping it in DPAPI like `trust.json.dpapi` is a
contained change if that trade-off is revisited.

## Pausing

### The two gates

`SyncPause` owns both and answers one question, `ShouldSendTo(did)`:

```
send  ⇔  not globally paused  ∧  that peer is not muted
```

They are **independent gates, not one shared switch**. Resuming a single
peer must not defeat a global pause, and resuming globally must not
un-mute a peer the user muted on purpose. Both directions are tested;
getting this wrong is the kind of bug that only shows up as "it started
syncing something I had told it not to".

The exclusion check is a third, earlier gate on the same path. An item is
sent to a peer only if no gate is closed. The three do not interact and
are deliberately not merged: they answer different questions — *may this
item leave at all*, and *may anything leave, for this peer*.

### Where it hooks in

`PeerRegistry.Broadcast` consults a `Func<string,bool>? ShouldSendTo`
once per connected peer. Null means send to everyone, which is what the
registry did before pausing existed. The registry is not told what a
pause is; the app wires the predicate to `SyncPause` at startup.

Ordering on the send path, which matters:

1. The item is built by the clipboard watcher.
2. Echo suppression (`ConsumeRecentWrite`) — must stay first, or the
   marker survives into the next copy and causes a spurious echo.
3. The excluded-app check — drops the item entirely.
4. Per-peer `ShouldSendTo` in the broadcast loop.

Steps 3 and 4 both build the item before discarding it, which is wasted
work for a large image while paused. Accepted deliberately: it keeps the
receive path and the watcher untouched, and copies are infrequent.

### Feedback

A paused device must say so where the user already looks:

- The tray icon carries a **pause badge**, composed at runtime over the
  real app icon so it cannot drift out of step with it.
- The tray tooltip reads "ClipSync — paused".
- The popup title reads "ClipSync — Paused".
- A muted peer's row reads "Paused" rather than "Online", because the
  reason nothing reaches it is the mute, not the network.

Both controls are labelled with the **verb**, not the state: the button
says what pressing it will do.

Every send decision is logged, **both branches**. Logging only the
suppressed case makes the absence of a log line ambiguous — it cannot be
told apart from the item never reaching the send path — and that
ambiguity produced a test that looked like it passed when it had not.

## UI

### Entry point

`TrayPopup` carries, in order: the peer list, `Settings…`, the global
pause toggle, and `Quit`. Each peer's row carries its own pause toggle,
on the right-hand edge.

`SettingsWindow` is a normal window, not a popup. `TrayPopup` hides on
deactivation, so a dialog opened from it would dismiss its own parent.
The settings window therefore opens *beside* the popup rather than over
it, and the popup suppresses its own dismissal while one of our windows
is taking focus.

Peers are data and sit in a card group; the commands are buttons. A
command restyled into a card row has to reimplement focus, hover and
contrast-theme behaviour that a real button already has.

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

## macOS (specified, not yet built)

Both features are still to build here. The practical companion to this
section — what the Windows implementation taught, and which parts of this
spec were wrong — is
`docs/superpowers/plans/2026-08-14-excluded-apps-macos-handoff.md`. Read
this for what to build and that for what will bite you.

### Excluded apps

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

### Pausing

Nothing platform-specific in the model — the gates, the persistence rule
and the send-only scope carry over unchanged. What differs is only where
things attach:

- **State** — a `SyncPause` equivalent reading and writing the shared
  `pausedPeers` key. That key is already defined above; the mac side
  should use it rather than invent another, so a settings file moved
  between machines still means what it says.
- **Gate** — one closure consulted per peer inside
  `PeerRegistry.broadcast`, mirroring the Windows predicate.
- **Global control** — an item in `MenuBarView` between Settings and
  Quit, with the state also in the popover's header.
- **Per-peer control** — a ⏸ / ▶ button on each `PeerRow`.
- **Menu-bar icon** — the paused state should show on the status item,
  which is the macOS analogue of the tray badge. `NSStatusItem` takes an
  `NSImage`, so the natural approach is a second template image or a
  composed one, rather than Windows' runtime GDI+ badge.

The Windows tests worth mirroring are the ones that pin the gates as
independent, and the persistence asymmetry: a mute survives a relaunch, a
global pause does not.

## Error handling

Every failure path falls open and logs **once**, not per copy.

| Failure | Behaviour |
|---|---|
| `SetWinEventHook` fails | Tracker permanently returns null; everything transmits; one startup log line |
| Process image / package lookup fails | `OpenProcess`/`QueryFullProcessImageName`/`GetPackageFamilyName` return a failure → null → transmits. This is known to cover protected/system processes and PID-teardown races. **Open question, not yet verified: does it also cover ordinary UAC-elevated apps?** `PROCESS_QUERY_LIMITED_INFORMATION` was specifically designed to let a medium-integrity caller query a higher-integrity process's image path, so elevated apps may resolve fine — nothing has measured this either way. Confirm empirically (elevate a foreground app, exclude it, copy) before documenting elevation as covered or not covered. |
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

- `SyncPause` — the global gate stops every peer; a mute stops one and
  leaves the others; **un-muting a peer while globally paused still sends
  nothing**, and **resuming globally leaves a muted peer muted**; DIDs
  compare case-insensitively; a mute survives a reload and a global pause
  does not; muting is idempotent; a blank DID is ignored; and mutes
  round-trip alongside excluded apps without either clobbering the other.

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
8. Pause globally; copy; confirm the log says the send was skipped, and
   that the item still reached the local clipboard.
9. Resume; copy; confirm the log says it was sent.
10. Mute one peer with the global pause off; confirm that peer is skipped.
11. Restart; confirm the mute survived and the global pause did not.
12. Confirm the tray icon carries the pause badge while globally paused.

End-to-end steps need kodachrome awake on the LAN; a sleeping Mac looks
identical to a discovery failure.

**Read the log, do not infer from silence.** Both branches of the send
decision are logged precisely so that "no line" is never the evidence.
An early run of step 10 produced no output and looked like a pass; the
copy had not reached the send path at all.

## Open follow-ups (out of scope)

- macOS implementation of both features, per the design above and the
  handoff doc.
- `TransferLog` is currently dead code on both platforms, so
  `transfers.log` will not show suppressed-vs-sent counts until it is
  wired up. Unrelated to this feature, noted because it would otherwise
  be the obvious way to verify step 1 above.
