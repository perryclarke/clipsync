# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Three implementations of one application: a LAN-only, end-to-end encrypted
clipboard sync tool for macOS 15+ (`clipsync-mac/`, Swift 6 / SwiftUI
`MenuBarExtra`), Windows 11 (`clipsync-win/`, .NET 10 / WinUI 3 tray app),
and Ubuntu 26.04+ (`clipsync-linux/`, .NET 10 daemon with a
StatusNotifierItem tray).

The mac and Windows halves share no code — they interoperate only by
conforming to `PROTOCOL.md` and a shared on-disk settings JSON schema.
`clipsync-linux` is the exception: it *links* the portable protocol sources
straight out of `clipsync-win` (see below), so that tree must not be edited
to accommodate Linux.

`PROTOCOL.md` is the source of truth for the wire format and state machines.
Change it *first*, then both implementations. `HANDOFF.md` is a running log of
design decisions, per-platform verification status, and where the code
deviates from the original plan (notably: trust is two-sided TOFU, not the
SPAKE2 PIN enrollment §7 still describes).

## Building and testing

Only the Linux client builds in this checkout. There is no Swift toolchain,
and `clipsync-win` targets a Windows-only TFM
(`net10.0-windows10.0.26100.0`). Work on the other two is
read/write-and-hand-off; the receiving machine compiles. Say so plainly
rather than claiming a change is verified.

**macOS** (needs Xcode's toolchain; Command Line Tools swift often can't read
the manifest):

```bash
swift build --package-path clipsync-mac                 # or -c release
swift test  --package-path clipsync-mac                 # full suite
swift test  --package-path clipsync-mac --filter StreamPlannerTests
CLIPSYNC_TLS_LOOPBACK=1 swift test --package-path clipsync-mac --filter StreamLoopback
clipsync-mac/build-dmg.sh                               # → dist/ClipSync.dmg
```

`StreamLoopbackTests` and `PeerCertBindingTests` are opt-in behind
`CLIPSYNC_TLS_LOOPBACK=1` — they need keychain access and loopback
networking, so they are skipped in a plain `swift test`.

**Windows:**

```powershell
dotnet build clipsync-win/ClipSync/ClipSync.csproj -c Release
dotnet test  clipsync-win/ClipSync.Tests/ClipSync.Tests.csproj
dotnet test  clipsync-win/ClipSync.Tests/ClipSync.Tests.csproj --filter FullyQualifiedName~StreamPlanner
.\clipsync-win\build-msi.ps1 -Arch x64        # -arm64, -SkipSign; → dist/ClipSync.msi
```

**Linux** (the one that builds here):

```bash
dotnet build clipsync-linux
dotnet test  clipsync-linux/ClipSync.Linux.Tests/ClipSync.Linux.Tests.csproj
dotnet run   --project clipsync-linux -- --debug     # console harness: peers, trust, pause
dotnet run   --project clipsync-linux -- --self-test # clipboard round trip; needs a real X display
clipsync-linux/build-deb.sh amd64                    # → dist/clipsync_<ver>_amd64.deb
```

Diagnostics go to stderr (`--debug` or `CLIPSYNC_DEBUG=1`), which as a
systemd service means `journalctl --user -u clipsync`. The tray needs a
StatusNotifierItem host: on GNOME that is
`gnome-extensions enable ubuntu-appindicators@ubuntu.com`, which ships
installed but disabled.

Windows debug logging is off by default: `--debug` (or `-d`, `/debug`),
`CLIPSYNC_DEBUG=1`, or an empty `debug-enabled` file beside the log. Output
goes to `%LOCALAPPDATA%\ClipSync\debug.log`. On macOS there is no log file —
run the binary directly and read stderr (`NSLog` from an `open`-launched
bundle does not surface via `log stream`).

Both apps accept `--reset` to clear the trust store at launch (before identity
load / keychain prompts) so peers must re-trust.

## Architecture

The two trees deliberately mirror each other file-for-file; when adding
something, put it in the same place on both sides.

| Concern | macOS | Windows |
|---|---|---|
| Composition root | `ClipSyncApp.swift` (`AppCoordinator`) | `App.xaml.cs` / `Program.cs` |
| mDNS advertise + browse, dial | `Net/Discovery.swift` | `ClipSync/Net/Discovery.cs` |
| One mTLS connection per peer | `Net/PeerConnection.swift` | `ClipSync/Net/PeerConnection.cs` |
| Peer list + fanout + tie-break | `Net/PeerRegistry.swift` | `ClipSync/Net/PeerRegistry.cs` |
| CBOR frame codec | `Net/Protocol.swift` | `ClipSync/Net/Protocol.cs` |
| Identity, trust store | `Security/` | `ClipSync/Security/` |
| Pure, unit-tested logic | `Net/Stream*`, `Clipboard/`, `Settings/`, `Sync/` | `ClipSync.Core/` |

`clipsync-linux` mirrors the same concerns but composes differently:
`Platform/` holds swappable per-flavour seams (`IClipboardBackend`,
`IDiscoveryBackend`, `ISecretStore`) with one implementation each today —
X11/XFixes, Avahi over D-Bus, and owner-only files. Everything above those
seams is linked from `clipsync-win`.

`ClipSync.Core` on Windows exists to hold exactly the platform-free logic the
tests cover — planner, assembler, suppression, foreground ring, settings,
pause. The Mac has no equivalent split; those files just live in the main
target. Keep new pure logic in `ClipSync.Core` so it stays testable.

`AppCoordinator` (Mac) is the wiring diagram worth reading first: watcher →
`peers.broadcast`, `peers.onRemoteItem` → writer, and a single
`peers.shouldSendTo` predicate that folds the global pause and per-peer mutes
together so the registry never learns what a pause is.

### Invariants that span both implementations

- **`did` = lowercase hex SHA-256 of the raw X9.63 EC public point** (65
  bytes) of the device's P-256 cert — *not* of the SPKI, whatever
  `PROTOCOL.md` §2 says. All three clients hash the point; a client written
  to the spec computes a different id and can never pair, with nothing in
  any log to explain why. See `HANDOFF.md`, and the golden-vector tests in
  `clipsync-linux/ClipSync.Linux.Tests/IdentityTests.cs`.
- **Peer identity binds at the TLS handshake**, from the verified
  certificate, never from the `Hello` (see commit `7a03b72`).
- **Trust is two-sided TOFU.** A pending peer shows a Trust button; the first
  side's connect fails until the second side also trusts. After a successful
  `Hello`, both auto-promote into the persistent trust store.
- **Size policy (§10).** ≤ 64 KiB inline, larger formats streamed in ≤ 1 MiB
  `FileChunk`s, item cap 100 MiB enforced by the sender (drop formats in
  order, log each drop). Streaming only to peers advertising the `stream`
  capability. Feature gating is on `caps`, never on `ver`.
- **Loop suppression.** A remote item written locally stamps
  `SHA-256(canonical bytes)` into a 5 s TTL set; the local watcher drops a
  change whose hash matches. Plus: drop anything whose `origin_did` is ours.
- **The settings JSON schema is shared.** `~/Library/Application
  Support/ClipSync/settings.json` and `%LOCALAPPDATA%\ClipSync\settings.json`
  hold the same keys (`excludedApps`, `pausedPeers`, `hiddenPeers`). Match the
  shape exactly — DIDs lowercased, blank names falling back to the 8-hex
  fingerprint, unknown `kind` values skipped rather than treated as errors, so
  each platform tolerates the other's entries. Trust lives separately
  (`trust.plist`; `trust.json.dpapi`).
- **Linux differs in three documented ways**, none of them bugs: no OS
  clipboard history, excluded apps match X11/XWayland apps only (a
  Wayland-native copy is owned by the compositor's bridge and cannot be
  attributed), and the PRIMARY selection is never synced. See the README.
- **Versions move in lockstep** across `clipsync-mac/Info.plist`
  (`CFBundleShortVersionString` + `CFBundleVersion`),
  `clipsync-win/ClipSync/ClipSync.csproj` (`<Version>`),
  `clipsync-win/installer/ClipSync.wxs` (`Version="x.y.z.0"`), and
  `clipsync-linux/ClipSync.Linux.csproj` (`<Version>`). Currently 0.8.0. Compatibility is signalled by `caps`, not the version number.

## Working conventions

- **Feature parity is the default.** Behaviour is identical on both platforms;
  only UI chrome differs, and even the wording is kept parallel ("Hidden
  devices", "Start over"; "Start ClipSync when you sign in" ↔ "Open ClipSync
  at login"). Whichever platform lands a feature first writes the other side's
  version, then appends a dated *Handoff* section to `HANDOFF.md` saying what
  was written, what was compiled, what was actually verified live, and what is
  still unverified.
- **Non-trivial features get a design doc** in `docs/superpowers/specs/`
  (`YYYY-MM-DD-name-design.md`) before implementation; multi-step plans go in
  `docs/superpowers/plans/`.
- **README.md is user-facing and kept current** — features, build steps, and a
  *Known gaps* list. Update it when behaviour changes.
- Commit subjects are imperative sentence case, no prefixes ("Stream clipboard
  formats over 64 KiB, cap items at 100 MiB"); bodies explain the reasoning.
- `tools/clipfuzz-mac` and `tools/clipfuzz-win` are clipboard fuzzers for
  exercising the watchers and writers by hand.
