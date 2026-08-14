# Send-side controls on macOS — handoff

**Scope:** two features, both shipped on Windows and neither started on
macOS — **excluded apps** and **pausing** (global and per-peer).

**Status:** Windows shipped and merged to `main`. macOS not started.
**Design of record:** `docs/superpowers/specs/2026-08-09-excluded-apps-design.md`
— now covers both features, with a macOS section for each.
**Windows plan, for reference:** `docs/superpowers/plans/2026-08-09-excluded-apps-windows.md`

This document is the thing the spec cannot be: a list of what the Windows
implementation actually taught us, including the parts the spec got wrong.
Read the spec for *what to build*. Read this for *what will bite you*.

Everything here was learned by building and driving the Windows side. None
of it is speculation about macOS behaviour, and where I am guessing about
macOS I say so.

The two features share a settings file, a send path, and one principle —
**everything here governs sending; receiving is never affected**. Build
them together; separating them means touching the same three places twice.

---

## 1. What the feature is

Items copied while an excluded app is frontmost are **not transmitted to
peers**. They are still placed in the local clipboard and the local
clipboard history. The exclusion suppresses *transmission only* — never
the copy itself. A user who excludes their password manager still gets
copy/paste inside that machine; they just do not get it on the other one.

---

## 2. The three invariants

These are load-bearing. Two of the three were violated at some point in the
Windows work and each cost a round of rework.

### 2.1 Fail open, always

If the frontmost app cannot be identified, **transmit**. Never suppress on
uncertainty. A clipboard that silently stops syncing is a far worse failure
than one that syncs something the user half-expected to be private.

This was violated on Windows without anyone noticing: before the decision
was extracted into a testable `SuppressionPolicy`, a throw inside the
foreground lookup fell into `OnChangedAsync`'s bare `catch` and dropped the
item — failing *closed*. The refactor fixed it incidentally. On macOS, put
the decision in one small testable function from the start and make the
unresolvable case an explicit, tested branch rather than an exception path.

### 2.2 The stored key must equal the key the matcher produces

The picker writes a key into `settings.json`. At copy time, the foreground
resolver produces a key and compares. **If those two can ever disagree, the
exclusion silently never fires**, and nothing in the UI says so — the app is
listed as excluded and simply is not.

On Windows this was a real, shipped-then-fixed defect. The picker keyed a
desktop app on its Start Menu shortcut's target executable; launcher-based
apps (Proton VPN's `ProtonVPN.Launcher.exe`, anything Squirrel-packaged)
run their UI from a *different* executable, so the stored key never matched.
Neither the plan nor the spec had ever stated the invariant, which is why
nobody checked it.

**macOS should be immune by construction** — `NSRunningApplication
.bundleIdentifier` at copy time and `CFBundleIdentifier` from the scanned
`.app` are the same string from the same source. Confirm this rather than
assume it, especially for:

- apps whose helper process is frontmost (Electron apps, anything with an
  XPC/helper architecture) — does `frontmostApplication` report the parent
  bundle or the helper?
- apps launched from a wrapper or shim
- `.app` bundles nested inside other bundles

If any case disagrees, ship the equivalent of the Windows escape hatch
described in §5.

### 2.3 Loop suppression runs *before* the exclusion check

In `PasteboardWatcher.tick()` the order is:

1. `writer.consumeRecentWrite(matching:)` — the echo guard
2. the exclusion decision
3. `onLocalCopy`

If the exclusion check short-circuits first, the recent-write marker
survives into the *next* copy and causes a spurious echo. This ordering is
already commented in the Windows `ClipboardWatcher.cs`; keep it on macOS.
The mac `tick()` already calls `consumeRecentWrite` first — just insert the
exclusion check after it, not before.

---

## 3. macOS-specific: the polling window

This is the one place the two platforms deliberately diverge, and it is
already decided in the spec — restating because it is easy to "fix" by
mistake.

`PasteboardWatcher` polls `NSPasteboard.changeCount` every 200 ms, so the
copy time is only known to within that interval. The rule:

> If **any** app held focus during the interval preceding the tick and that
> app is excluded, suppress.

This is stricter than Windows, where the exact copy timestamp is available
and exactly one app owns the moment. The alternative — take whoever is
frontmost at tick time — leaks on a fast copy-then-switch, which is
precisely the sequence a user performs when copying a password and
switching to the app that needs it. **Do not mirror this back to Windows.**

Practical consequence: the mac ring needs to answer "which apps held focus
between t1 and t2", not just "which app held focus at t". That is a
different query from the Windows one. Build the ring API for it.

---

## 4. The foreground ring

Windows: `clipsync-win/ClipSync.Core/Clipboard/ForegroundRing.cs` (16
entries, 2-minute retention). macOS should mirror the semantics.

- **Half-open intervals `[t_i, t_{i+1})`.** A timestamp falling exactly on a
  transition resolves to the *newly activated* app. Pick this explicitly and
  test it; it is the kind of off-by-one that only shows up as a rare
  mis-suppression that nobody can reproduce.
- Bounded in both size and age; eviction returns "unknown", which means
  transmit (§2.1).
- Windows trims the ring on write. The first version mutated the list while
  iterating downward over it — caught in plan review, never shipped. Write
  the trim as a filter, not an in-place walk.

`NSWorkspace.shared.notificationCenter` /
`didActivateApplicationNotification` is the mac source. Record the
timestamp when the notification arrives.

---

## 5. Capturing "the app I'm using now"

Windows ships an **"Exclude the app I switch to…"** action alongside the
installed-apps picker: it counts down five seconds while the user switches,
then records whatever the foreground tracker says. It exists precisely
because §2.2 can fail — it records the identity *through the same resolver
the matcher uses*, so by construction it cannot disagree.

If macOS satisfies §2.2 cleanly, this is optional. Two things to carry over
if it is built:

- **Require an actual focus transition during the countdown.** The first
  version excluded whatever was in front before the settings window opened
  — realistically Finder — when the user watched the countdown without
  switching. Excluding Finder silently kills syncing for every Finder copy
  with nothing tying cause to effect. Windows counts ring transitions
  rather than comparing identities, because switching away and back records
  two transitions while never switching records none, and those are
  indistinguishable by identity alone.
- **Never let the app exclude itself.** ClipSync's own windows are kept out
  of the Windows ring by `WINEVENT_SKIPOWNPROCESS`; on macOS filter on
  bundle identifier explicitly.

There is a known cosmetic wart: a captured app is stored with the name the
*resolver* produces, not the friendly one, so Windows shows `acrodist`
where the picker would have said "Adobe Acrobat Distiller". macOS gets
`localizedName` from `NSRunningApplication`, so this likely does not arise
— check it.

---

## 6. Settings file

`~/Library/Application Support/ClipSync/settings.json`, alongside the
existing `trust.plist` and `transfers.log`. Schema is in the spec; macOS
entries are `kind: "bundle"` keyed by bundle identifier.

- **Unknown `kind` values are ignored on load, not treated as errors.** This
  is what lets a Windows-written file load on macOS and vice versa. Windows
  has a test for exactly this (`AppSettings` ignores `kind: "bundle"`);
  write the mirror test that macOS ignores `exe` and `package`.
- Parse failure yields an **empty exclusion list**, logged — matching
  `TrustStore.load()`. It does not throw and does not fall back to a partial
  list.
- Write atomically (write-then-move). Windows does; a torn settings file
  means the user's exclusions silently vanish on next launch.
- The file names apps the user considers sensitive. It is deliberately
  unencrypted for hand-editability and relies on directory permissions.
  Same decision as Windows; revisit together if at all.

---

## 7. UI

The mac UI is a `MenuBarExtra` with `.menuBarExtraStyle(.window)`
(`ClipSyncApp.swift`), and `MenuBarView.swift` is the popover.

- Add a `Settings…` button above `Quit ClipSync` in `MenuBarView`.
- Settings must be a **separate window**, not popover content. The popover
  dismisses on deactivation, so it cannot host a file picker or a modal.
  This is the same constraint that ruled it out on Windows.
- Windows additionally keeps its tray popup *open* alongside the settings
  window and positions the window beside it. Whether that is worth doing on
  macOS is a judgement call — `MenuBarExtra` dismissal is less controllable
  than a hand-rolled Win32 popup, and this may not be worth fighting.
- The Windows settings window is a `SettingsExpander` group: header, a
  scrolling list of `SettingsCard` rows, and a fixed add row. The macOS
  equivalent is a plain SwiftUI `Form`/`List` in a `Settings` scene; do not
  port the layout, port the shape (group header, scrollable list, add
  control that does not scroll away).

**Two UI lessons that generalise:**

- The list must scroll *inside itself*, not scroll the whole page. On
  Windows the page-level scroller pushed the add button off the bottom, so
  the way to add an app was to scroll looking for the button that had gone.
- Show a **partial row** (Windows shows 2.5) so it is visible that scrolling
  reveals more. A whole number of rows looks like the whole list.

**Accessibility** — the mac side should get the same treatment the Windows
side did: every control named, per-row buttons naming their row ("Stop
excluding 1Password", not "Remove"), decorative icons hidden from the
accessibility tree, and status changes announced. SwiftUI gives most of
this for free via `.accessibilityLabel`, but per-row button labels are the
part that is always wrong by default.

---

## 8. Installed-apps picker

Scan `/Applications`, `/System/Applications`, `~/Applications` for `.app`
bundles; read `CFBundleIdentifier`, `CFBundleName`, and the bundle icon.
`Browse…` uses `NSOpenPanel` restricted to application bundles.

From the Windows equivalent:

- **Enumeration is slow** (a few hundred ms there). Do it off the main
  thread. On Windows the icons had to be carried as raw PNG bytes and
  converted to UI types on the UI thread, because the UI image type has
  thread affinity — check whether `NSImage` has the same constraint before
  building on a background queue.
- **Distinguish "enumeration failed" from "everything is already
  excluded"** and from "your search matched nothing". They are three
  different messages and showing the wrong one sends the user down the
  wrong path. Windows initially conflated the first two.
- **The search box has focus before enumeration finishes.** Windows
  discarded anything typed during that window and then displayed the query
  above an unfiltered list. Apply the current filter when loading
  completes, do not reset to "show everything".
- Windows merges rows that share a matching key (a dozen Start Menu
  shortcuts share `cmd.exe`) and labels them "… (and 11 others)". macOS
  keyed on bundle identifier should not need this — one bundle, one row.

---

## 9. What to test

Windows carries 39 xunit tests in `clipsync-win/ClipSync.Tests/`. The mac
side has no test target today; adding one is in scope.

Worth testing (all of these caught or would have caught a real Windows bug):

- Identity equality on kind+key only — display name and path do not affect it.
- Settings round-trip; corrupt file → empty list; unknown `kind` ignored;
  add/remove idempotent.
- Ring: query before any transition → unknown; inside an interval → that
  interval's app; after eviction → unknown; a timestamp exactly on a
  transition → the newly activated app.
- Ring interval query (§3): an app that held focus for part of the polling
  window is found.
- The suppression decision itself, including the fail-open branch.
- The two pause gates as *independent*: un-muting a peer while globally
  paused still sends nothing, and resuming globally leaves a muted peer
  muted. Plus the persistence asymmetry — a mute survives a relaunch, a
  global pause does not — and that mutes round-trip alongside excluded
  apps without either clobbering the other in the shared file.

**And one end-to-end check that no unit test substitutes for:** exclude a
real app, copy inside it, and confirm from the log that the item was
suppressed *and* that the same copy still reached the local clipboard. Then
the control: copy with a non-excluded app frontmost and confirm it
transmits. On Windows both directions were verified this way and the first
attempt at the control was **inconclusive rather than passing** — the test
harness silently failed to set the clipboard, so "no suppression logged"
proved nothing. Verify the clipboard actually changed before believing a
negative result.

---

## 9a. The other half: pause / resume

Now specified in the design doc alongside the exclusions; this section is
only the field notes. Worth doing in the same pass, because it lands in
the same three places: the send path, the settings file, and the menu.

- **Global** — a *Pause syncing* item in `MenuBarView` between Settings
  and Quit, plus the state in the popover title. Not persisted.
- **Per-peer** — a ⏸ / ▶ button on each `PeerRow`, muting sending to that
  peer. Persisted in `settings.json` as `pausedPeers`, a list of
  lowercase DID hex. That key already exists in the shared file, so the
  mac side should read and write it rather than inventing another.
- **Send only.** Items from peers still arrive and are still applied
  while paused. Nothing is queued; nothing replays on resume.
- The gate belongs in `PeerRegistry.broadcast`, as one predicate covering
  both cases, so the registry never learns what a pause is. Windows uses
  `Func<string,bool>? ShouldSendTo`; the Swift equivalent is a closure.

The rule the Windows tests pin down: the two gates are independent.
Un-muting one peer must not defeat a global pause, and resuming globally
must not un-mute a peer. See `SyncPauseTests`.

One thing learned verifying it, which applies to any of this: log both
branches of the send decision. Logging only the skip meant "no skip line"
was indistinguishable from "the item never reached the send path", and a
test that looked like it passed was actually inconclusive.

Two more from the Windows build, both cheap to avoid and annoying to
find:

- **Show the paused state where the user already looks.** A tooltip needs
  hovering to find. Windows badges the tray icon; the macOS analogue is
  the `NSStatusItem` image. Also put it in the popover header, and make a
  muted peer's row say "Paused" rather than "Online" — the reason nothing
  reaches it is the mute, not the network.
- **Label the controls with the verb, not the state.** The button says
  what pressing it will do. It is the title and the row text that report
  what is currently true.

## 10. Open questions

- **Elevated / privileged apps (Windows, still open).** Can an app running
  elevated be excluded? `PROCESS_QUERY_LIMITED_INFORMATION` exists
  specifically so a medium-integrity caller can query a higher-integrity
  process's image path, so it may work fine — nothing has measured it. The
  Windows resolver now logs the Win32 error code, so the answer is one
  experiment away. The macOS analogue is whether `frontmostApplication`
  reports usefully for privileged or hardened-runtime apps.
- **Helper processes (macOS, unknown).** See §2.2.
- **Secure input / password fields.** macOS has `EnableSecureEventInput`;
  an app holding secure input is a strong hint that whatever is being
  copied is sensitive. Out of scope here, but it may be a better signal
  than an app list, and worth a thought before building more UI.

---

## 11. Reference: the Windows files

| Concern | File |
|---|---|
| Identity model | `clipsync-win/ClipSync.Core/Settings/AppIdentity.cs` |
| Settings persistence (both features) | `clipsync-win/ClipSync.Core/Settings/AppSettings.cs` |
| Pause state and the two gates | `clipsync-win/ClipSync.Core/Sync/SyncPause.cs` |
| Per-peer send gate | `clipsync-win/ClipSync/Net/PeerRegistry.cs` |
| Tray controls and paused icon | `clipsync-win/ClipSync/UI/TrayPopup.xaml{,.cs}`, `UI/TrayIcon.cs` |
| Foreground ring | `clipsync-win/ClipSync.Core/Clipboard/ForegroundRing.cs` |
| Foreground tracker | `clipsync-win/ClipSync.Core/Clipboard/ForegroundTracker.cs` |
| Window → app resolution | `clipsync-win/ClipSync.Core/Clipboard/Win32WindowResolver.cs` |
| The decision, testable | `clipsync-win/ClipSync.Core/Clipboard/SuppressionPolicy.cs` |
| Call site | `clipsync-win/ClipSync/Clipboard/ClipboardWatcher.cs` |
| Installed-apps enumeration | `clipsync-win/ClipSync/UI/InstalledApps.cs` |
| Settings UI | `clipsync-win/ClipSync/UI/SettingsWindow.xaml{,.cs}` |
| Picker | `clipsync-win/ClipSync/UI/AppPickerDialog.xaml{,.cs}` |
| Tests | `clipsync-win/ClipSync.Tests/` |

The mac equivalents land under `clipsync-mac/Sources/ClipSync/`, following
the existing `Clipboard/`, `Security/`, `UI/` split.

---

## 12. macOS verification results (2026-08-14)

Both features are now built and verified on the Mac. Findings that answer
this document's open questions, plus one bug the verification surfaced:

- **§2.2 holds on macOS, verified rather than assumed.** For every app
  tested — Safari, TextEdit, 1Password (Electron), Claude (Electron),
  Proton VPN (the app whose launcher broke the Windows build) — the
  bundle identifier reported by `didActivateApplicationNotification` /
  `frontmostApplication` equals the `CFBundleIdentifier` read from the
  scanned `.app`, after one shared normalisation. Electron helpers are
  not reported; the parent bundle is. The §5 capture escape hatch is
  therefore **not built** — nothing needs it.
- **End-to-end, log-verified both ways** against a live Windows peer
  (DEEPTHOUGHT): copy in an excluded app → `suppressed item from
  TextEdit`, no broadcast line, item still on the local clipboard;
  control copy → `sending item` + `Broadcast: sending to cb909646`;
  muted peer → `Broadcast: not sending to cb909646 (paused)`; resume via
  the popover button → next copy sent, no restart.
- **Bug found by the "log both branches" rule:** `Timer.scheduledTimer`
  registers in the run loop's default mode only, so watcher ticks
  stalled while any menu or popover was tracking, and rapid copies
  coalesced. The timer is now added in `.common` mode. The pre-feature
  watcher had the same latent bug.
- **macOS 26 pasteboard privacy:** programmatic reads are gated per app
  (`NSPasteboard.accessBehavior`). The watcher now logs the stance at
  startup and logs a change that yields no readable formats — without
  those lines a denied read is indistinguishable from no copy at all.
- **Keychain prompts:** ad-hoc signing (`codesign --sign -`) gives every
  build a new code identity, so the TLS-identity keychain items re-prompt
  (~5 dialogs) on each rebuild. `build-dmg.sh` now prefers a real signing
  identity (Apple Development / Developer ID) when one is present; with a
  stable identity the prompts are one-time.
- The polling-window rule (§3) ran as specified and produced no
  surprising suppressions during testing. No change proposed.

## 13. Build note

Only the Windows side can be built or tested on the current development
machine. **Swift changes must be written conservatively and verified by
Perry on the Mac** — assume no compile-check feedback loop while writing
them, and prefer obvious code over clever code accordingly.
