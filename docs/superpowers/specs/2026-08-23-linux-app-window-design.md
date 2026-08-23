# Linux app window — design

Date: 2026-08-23
Status: implemented, both stages (window + tray rework, then excluded
apps, start-at-login, Start over). Stage 1 verified live end-to-end;
stage 2 click-through pending — see the 2026-08-23 handoff in
`HANDOFF.md`.

## Summary

Replace the flat tray menu as the primary Linux UI with a real window:
GTK4 + libadwaita, in the same process as the daemon, opened by clicking
the tray icon. The tray menu shrinks to a three-item fallback (Open,
Pause, Quit) shown on right-click.

This is a UX decision forced by the platform. GNOME's Quick Settings
look — disclosure rows, toggles that keep the popup open — is Shell-drawn
UI that third-party apps cannot produce; everything arriving over
StatusNotifierItem + dbusmenu renders as a flat list of one-shot actions
that closes on every click (see `Ui/TrayMenu.cs` and the memory record —
both richer menu shapes were built and failed against the AppIndicator
extension). The current everything-visible flat menu works but is clunky
(popup closes on every click) and looks nothing like the mac popover or
Windows flyout.

The window also absorbs the missing settings surface — the largest
recorded gap in the port: excluded apps (console-only today), "Start
over", and start-at-login (both unimplemented).

## Goals

- One left click on the tray icon opens (or focuses) a window with the
  device list: Trust with fingerprint for pending peers, per-device pause,
  hide/unhide — as real buttons and switches that do not dismiss the UI.
- Global pause with the same "verb, not state" labelling as the other
  platforms.
- Settings in the same window: excluded apps, start-at-login, Start over.
- Native GNOME appearance (libadwaita boxed lists), parallel wording with
  the other two clients.
- The daemon still runs headless: no display, no SNI host, or GTK failing
  to initialise must not take sync down.

## Non-goals

- A popover hanging off the top bar. Under Wayland an app cannot position
  its own window, so this opens as a normal window. Accepted; it is the
  one part of the mac/Windows feel that cannot be reproduced.
- The Quick Settings look. Only a GNOME Shell extension could deliver it.
- An installed-apps picker like Windows'. Linux exclusions are keyed on
  `WM_CLASS` (see the port-gaps notes); the editor takes a class name
  directly, with the live list of entries and per-row Remove.
- Multi-window or per-peer detail pages. One window, one scrollable page.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| UI direction | In-process GTK4/libadwaita window | Native look; absorbs the planned settings window; no fourth codebase |
| Toolkit binding | Gir.Core (`GirCore.Adw-1` 0.8.1, NuGet) | Pure P/Invoke bindings, .NET 10 compatible; libadwaita ships on Ubuntu 26.04 |
| Tray click routing | `ItemIsMenu = false`; `Activate` opens the window | The SNI method meant for exactly this; Telegram uses it under the same extension |
| Fallback menu | Right-click dbusmenu: Open ClipSync / Pause Syncing / Quit ClipSync | Survives hosts that never route `Activate`; pause and quit stay one click away |
| Window lifetime | Hide on close; daemon keeps running | The window is a view over the daemon, not the app |
| GTK threading | One dedicated UI thread, started lazily on first open; all GTK calls marshalled to it | The daemon may start before any display exists; GTK must never run on D-Bus or network threads |
| Headless behaviour | UI thread refuses to start without `DISPLAY`/`WAYLAND_DISPLAY`; logged once; tray + console keep working | Sync must not depend on a display |
| View model | Pure `WindowModel` mapping state → row descriptions, unit-tested like `TrayMenu` | GTK itself is untestable in CI; the shape and wording are what regress silently |
| Start-at-login | `~/.config/autostart/clipsync.desktop`, written/removed by the switch | The XDG mechanism; works on GNOME and everything else; no systemd unit management from inside the app |
| Start over | Button in the window calling the same path as `--reset`, then dropping trusted peers back to Pending | Matches the other platforms' wording and effect |

### Rejected alternatives

**GNOME Shell extension.** The only way to get the actual Quick Settings
look. Rejected: a separate JavaScript codebase talking to the daemon over
D-Bus, GNOME-only, breaks on Shell version bumps, and needs the user to
install and enable it. The look is not worth a fourth implementation.

**Avalonia.** Pure .NET, no native binding risk. Rejected: visibly
non-native on GNOME, which is half of the complaint being fixed.

**Keep the flat menu as primary, window for settings only.** Least work.
Rejected: the clunky menu would remain the daily interface.

**Separate UI process.** A `clipsync-ui` binary the icon spawns. Rejected:
IPC surface for no benefit; the daemon already owns all the state, and
in-process was the plan for the settings window all along.

## Architecture

New files, all under `clipsync-linux/Ui/`:

| File | Responsibility |
|---|---|
| `UiThread.cs` | Owns the GTK thread and main loop; `Post(Action)`; refuses to start headless |
| `WindowModel.cs` | Pure state → rows/groups mapping, incl. all wording; unit-tested |
| `MainWindow.cs` | Renders `WindowModel` output as Adw widgets; wires buttons to `TrayActions` |

Modified:

- `Ui/TrayIcon.cs` — `ItemIsMenu=false`; `Activate`/`SecondaryActivate`
  invoke a new `OpenWindow` action.
- `Ui/TrayMenu.cs` — shrinks to Open / Pause / Quit.
- `Program.cs` — constructs the UI thread + window controller; peer
  changes refresh the window as well as the tray.
- `ClipSync.Linux.csproj` — adds `GirCore.Adw-1`.
- `build-deb.sh` — adds `libgtk-4-1`, `libadwaita-1-0` to Depends.

`TrayState` / `TrayActions` stay as the contract between the daemon and
both UIs; the window adds actions for exclusions, autostart, and reset.

## Window layout

Adw.ToolbarView with a header bar ("ClipSync"), content is one scrollable
Adw.PreferencesPage:

- **Syncing** group: `Pause Syncing` switch row (subtitle: "Nothing is
  sent while paused; items from other devices still arrive."). This
  device's name and 8-hex fingerprint as a dimmed row beneath.
- **Devices** group: one Adw.ActionRow per visible peer. Title = name,
  subtitle = the existing `Describe` strings ("Online, 0.8.0", "Waiting
  to be trusted", "Looking…", "Offline"; a muted peer shows "Paused").
  Suffix widgets: pending → `Trust` (suggested-action) + fingerprint in
  the subtitle, and a hide button; trusted → a send switch (off = muted)
  and a hide button. Empty state: "No devices found".
- **Hidden devices** group (only when non-empty): row per hidden peer
  with a `Show` button.
- **Excluded apps** group: row per `WM_CLASS` entry with Remove; an entry
  row + Add button. Explainer matches the other platforms: "Items copied
  while these apps are in the foreground are not sent to your other
  devices." Wayland-native caveat sentence from the README.
- **General** group: "Start ClipSync when you sign in" switch (Windows
  wording, since this tree links the Windows sources); "Start over"
  button with a confirmation dialog, wording matching mac/Windows.

Live updates: `peers.OnChange` (and settings changes) post a rebuild of
the affected group to the UI thread. Rebuild is coarse (regenerate group
contents from the model) — the lists are tiny.

## Threading

- The UI thread is created on first open request, runs `Adw`
  init + a GLib main loop, and stays alive; subsequent opens just present
  the window.
- Everything GTK happens on that thread via `Post` (GLib idle source).
- Action callbacks (Trust, pause, hide…) call the same daemon objects the
  tray menu calls today, from the UI thread — the same objects are
  already invoked from the D-Bus handler thread, so no new concurrency is
  introduced.
- If GTK init fails (no display, missing libs), the failure is logged
  once, `Open` becomes a no-op that logs, and everything else keeps
  running.

## Packaging

`GirCore.Adw-1` is managed code over P/Invoke; build needs no native
packages. Runtime needs `libgtk-4-1` and `libadwaita-1-0` (both in the
Ubuntu 26.04 default install) — added to the .deb Depends anyway.

## Risks / verify first

1. **Does ubuntu-appindicators route `Activate` when `ItemIsMenu=false`?**
   Resolved 2026-08-23, from the extension's source: a single left click
   never reaches `Activate` while a menu is attached (`ItemIsMenu` is not
   consulted; the extension introspects for an `Activate` method and still
   toggles the menu on single click). Double-click calls `Activate`,
   middle click `SecondaryActivate`. With no menu a single click does
   nothing, so the fallback menu's "Open ClipSync" is the single-click
   path, as this risk anticipated. See the 2026-08-23 handoff.
2. **Gir.Core API coverage.** 0.8.1 is pre-1.0; if a needed widget is
   unbound, fall back to plainer GTK4 (ListBox with `.boxed-list`), which
   the bindings do cover.
3. **GTK on a non-main thread.** Supported on Linux in practice, but if
   it misbehaves, invert the composition: GTK owns the main thread and
   the daemon moves to a worker — a Program.cs-only change.

## Testing

Unit (`ClipSync.Linux.Tests`):

- `WindowModelTests` — mirrors today's `TrayMenuTests`: pending peer
  offers Trust with fingerprint; muted peer shows "Paused" and an off
  switch; hidden peers listed with Show; empty state; callbacks receive
  full DIDs; excluded-apps rows round-trip add/remove.
- `TrayMenuTests` — rewritten for the three-item menu: Open first, Quit
  last, pause tracks state and invokes with the opposite value.
- Autostart: the .desktop writer/remover against a temp XDG_CONFIG_HOME.

Manual, on the real session:

1. Left-click the icon → window opens; again → focuses, not duplicates.
2. Right-click → three-item menu; all three work.
3. Trust a pending Mac from the window; two-sided TOFU completes without
   reopening anything.
4. Toggle a device's switch off; copy; confirm the log shows the skip.
5. Close the window; daemon keeps syncing; icon reopens it.
6. Kill the display (`unset DISPLAY` service run): daemon runs, log says
   the window is unavailable, tray fallback still pauses/quits.
7. Enable start-at-login; log out/in; daemon is running.
8. Start over: both sides back to Pending; re-trust works.

## Staging

1. Tray rework + window with Syncing/Devices/Hidden groups (the UX
   complaint), fallback menu, tests.
2. Settings: excluded apps editor, start-at-login, Start over.
3. README + HANDOFF updates; .deb dependency bump.
