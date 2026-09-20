# Debug section in Settings — design

Date: 2026-09-19. Status: implemented (macOS verified; Windows and Linux
written, not compiled — see HANDOFF.md).

## Problem

Turning on diagnostics meant knowing a different incantation per platform,
none of them discoverable from the app, and on macOS there was no log file
at all — only stderr from a binary you had to launch from a terminal
yourself. Asking someone for a log meant first teaching them how to produce
one.

The three stacks had drifted:

| | Sink | Gate |
|---|---|---|
| Windows | `%LOCALAPPDATA%\ClipSync\debug.log`, 5 MB → `.log.1` | `CLIPSYNC_DEBUG=1`, `debug-enabled` marker, `--debug` |
| Linux | stderr → journald (systemd `--user` unit) | same three |
| macOS | none — 25 ungated `NSLog` calls | none |

## Decisions

- **macOS gains a file logger** (`Sources/ClipSync/Logging.swift`) matching
  Windows byte-for-byte in gate, rotation and line format, so a log from
  either platform reads the same. It writes to
  `~/Library/Application Support/ClipSync/debug.log`, beside `settings.json`.
- **macOS keeps stderr unconditional.** Only the *file* sink is gated.
  Gating stderr would break the documented way the Mac is debugged ("run the
  binary directly and read stderr"), which is the one channel that has
  always worked — an `open`-launched bundle's NSLog does not surface via
  `log stream`.
- **Linux stays on journald.** It runs as a systemd `--user` service, so
  stderr is already captured, timestamped and rotated. Adding a second file
  would mean reimplementing rotation badly alongside the journal.
- **The toggle persists via the `debug-enabled` marker file**, not
  `settings.json`. The shared settings schema is therefore untouched and
  needs no cross-platform migration, and the marker was already the
  convention on two of the three platforms.
- **No log-level control.** No platform has levels; the gate is on/off. A
  level picker would have to invent the levels first.
- **Reset moves in with Debug.** "Start over" is the most destructive
  control in the app, and like Debug it is something you go looking for when
  something is wrong — not something to meet while browsing settings.

## Revealing it

Held **Option** (macOS) / **Alt** (Windows) at the moment Settings is
summoned. Read at the click — `NSEvent.modifierFlags` and
`GetAsyncKeyState(VK_MENU)` — and passed in, never stored. Both windows are
singletons that outlive one click, so both re-apply the flag on every show;
otherwise a second summon would keep the first one's answer.

**Linux shows it always.** A tray activation arriving over
StatusNotifierItem/dbusmenu carries no modifier state to read. That suits
the platform where the app is most often run from a terminal anyway.

## The section

Identical wording everywhere:

- **Debug logging** toggle → `Log.setEnabled` / `Identity.SetLoggingEnabled`,
  which flips the in-memory flag (immediate) and writes or removes the
  marker (survives relaunch).
- A caption promising **metadata only** — never clipboard contents, never
  key material. That promise is what makes a log safe to send to someone,
  and it is asserted in the tests.
- **macOS/Windows:** "Open log" and "Show in Finder"/"Show in folder",
  disabled until the file exists, with the path shown beneath.
- **Linux:** "Copy command", yielding `journalctl --user -u clipsync -f`.
  The copy goes through `ClipboardWriter.CopyLocally`, which reuses the
  writer's existing loop-suppression stamping so ClipSync does not push its
  own diagnostic command to every peer the moment you ask how to read the
  log.

## Testing

- macOS `LoggingTests` (8): silent until enabled, marker written on enable
  and removed on disable, marker alone enables a fresh launch, `--debug`
  enables without persisting, rotation at the threshold starts a clean file,
  line shape `HH:mm:ss.SSS message`, printf arguments expand.
- Linux `WindowModelTests`: the journal command is the one that reads this
  unit, and the metadata-only promise is still in the subtitle.
- Both branches of the gate are exercised live on macOS: no marker → the log
  does not grow; marker → it does.

## Out of scope

Log levels, a log viewer in-app, and moving Linux off journald.
