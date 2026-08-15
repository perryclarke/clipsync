# ClipSync

LAN-only, end-to-end encrypted clipboard sync between macOS 15+ and
Windows 11 25H2+. No cloud. mDNS + mTLS 1.3 over IPv6. Items land in
both the active clipboard and the OS clipboard history (macOS 15
Clipboard History / Win+V). Current release: **0.6.0** on both
platforms, kept in step because the two halves share a wire protocol
and a settings schema.

## Repository map

- **PROTOCOL.md** — wire format and state machines (source of truth for
  every implementation).
- **HANDOFF.md** — design decisions, what has been verified on each
  platform, and where the implementation deviates from the original plan
  (notably the trust flow — see *Known gaps*).
- **clipsync-mac/** — Swift 6 / SwiftUI `MenuBarExtra` app.
- **clipsync-win/** — .NET 10 / WinUI 3 tray app.
- **tools/clipfuzz-mac**, **tools/clipfuzz-win** — clipboard fuzzers used
  to exercise the watchers and writers.
- **dist/** — build outputs: `ClipSync.dmg`, `ClipSync.msi`, and
  `clipsync-codesign.cer` (see *Building*).

## Building

**macOS.** Run `clipsync-mac/build-dmg.sh`. It builds with
`swift build -c release` (preferring Xcode's toolchain over the Command
Line Tools one), refreshes `clipsync-mac/ClipSync.app`, signs it, and
wraps it in a drag-to-Applications `dist/ClipSync.dmg`. Signing uses an
*Apple Development* or *Developer ID Application* identity if one is in
the keychain, otherwise ad-hoc; ad-hoc works but gives every build a new
code identity, so the keychain re-prompts for the TLS key on each
rebuild. The app is not notarized — this is a personal-use tool, and
notarization is a separate future step if it is ever distributed.

**Windows.** Run `clipsync-win/build-msi.ps1` (`-Arch x64|arm64`,
`-SkipSign`). It publishes a self-contained build, Authenticode-signs
the binaries and the MSI with a self-signed code-signing certificate
(created on first run, kept in the CurrentUser store), and writes
`dist/ClipSync.msi` plus the public cert `dist/clipsync-codesign.cer`.
On a target machine, import that `.cer` into *Trusted Root Certification
Authorities* and *Trusted Publishers* to make the signature validate;
without it the MSI installs fine but shows as untrusted. The MSI
declares `MajorUpgrade`, so installing a newer version over an older one
replaces it; installing the *same* version does nothing. For
development, open `clipsync-win/ClipSync.sln` in Visual Studio 2022
with the Windows App SDK 1.8 workload, or
`dotnet build clipsync-win/ClipSync/ClipSync.csproj -c Release`.

**Debug logging (Windows).** Off by default. Turn it on with `--debug`
(also `-d` / `/debug`) on the command line, `CLIPSYNC_DEBUG=1` in the
environment, or an empty `debug-enabled` file next to the log. Output
goes to `%LOCALAPPDATA%\ClipSync\debug.log`.

## Features

Both platforms have the same behaviour; only the UI chrome differs.

### Pause / resume

Sending can be paused from the tray / menu-bar menu. **Pause Syncing**
stops this device sending anything to anybody; the title reads
"ClipSync — Paused" (and on Windows the tray tooltip follows), so it is
visible without opening the menu. Each known peer also has its own
pause — a ⏸ / ▶ button on Windows, an entry in the peer's submenu on
macOS — which pauses sending to just that machine. Both are send-only:
items from peers still arrive and still land in your clipboard while
paused, and nothing is queued or replayed on resume. A per-peer pause is
remembered across restarts; a global one deliberately is not, so a
reboot can never leave you silently not syncing.

### Excluded apps

Apps can be excluded from sync. Anything copied while an excluded app is
in the foreground stays local — it is still placed in your own clipboard
and clipboard history, but never sent to a peer. If ClipSync cannot
identify the foreground app — some system and protected processes cannot
be inspected — the item is treated as not excluded and is synced.

*macOS:* menu bar → **Settings…** → **Excluded apps** → **Add app…**
opens a picker of installed apps (or browse to any `.app`); **Remove** on a
row drops it. Apps are matched on bundle identifier.

*Windows:* tray menu → **Settings…**, which opens beside the tray popup
rather than over it, then **Add app** → **Choose an installed app…** and
pick from the installed-app list, which covers Store / UWP apps as well
as desktop ones. **Browse…** in that dialog picks an `.exe` directly, for
anything the list misses.

Desktop apps on Windows are matched on the executable's file name, which
the list reads from the Start Menu shortcut's target. Some apps start
through a stub — a launcher, or a Squirrel `Update.exe` — that is not the
executable owning the window you copy from, so picking them from the list
has no effect. If an exclusion does not take, use **Add app** →
**Exclude the app I switch to…** instead: it counts down five seconds
while you switch to the app, then records whatever is actually in the
foreground, which is by construction what the matching sees.
(**Browse…** to the real `.exe` works too.) Where several Start Menu
entries share one executable — a dozen share `cmd.exe` — they appear as a
single row labelled "… (and 11 others)", because excluding one excludes
all of them.

## Known gaps

Discovery, mTLS transport, CBOR framing, clipboard watchers and writers
with loop suppression, trust-store persistence, pause/resume, excluded
apps, and the menu-bar / tray UI are in place and internally consistent
across both codebases.

Trust is two-sided TOFU (see `HANDOFF.md`, *Trust flow*): a newly
discovered peer shows as untrusted with a **Trust** button; clicking it
on both sides pins the peer's key, and once a Hello completes both sides
promote it to the persistent trust store, so future launches reconnect
without clicks. That is the intended design for a LAN-only personal
tool.

One area is still short of the full `PROTOCOL.md` spec:

1. **Large-item / streaming flow.** Inline payloads (≤ 64 KiB) sync
   today. The `LargeItemOffer` / `LargeItemAccept` / `FileChunk` /
   `FileEnd` path (`PROTOCOL.md` §6.3–6.6) has its message types and the
   `stream_id` payload variant defined, but no codecs or handlers, so
   anything larger than 64 KiB is not yet transferred. The >100 MiB
   prompt UI and streamed file transfer are the remaining work.

The macOS self-signed `SecIdentity` builder (`Identity.swift`
`createKeychainIdentity()`) and its Windows counterpart
(`Identity.cs` `CreateSelfSignedCert()`) are **complete** — earlier
revisions of this list described them as stubs; they no longer are.
