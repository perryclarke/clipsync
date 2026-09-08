# ClipSync

LAN-only, end-to-end encrypted clipboard sync between macOS 15+,
Windows 11 25H2+ and Ubuntu 26.04+. No cloud. mDNS + mTLS 1.3 over IPv6. Items land in
both the active clipboard and the OS clipboard history where the OS has
one (macOS 15 Clipboard History / Win+V; Linux has no equivalent — see
*Platform differences on Linux*). Current release: **0.8.0** on all three,
kept in step because they share a wire protocol and a settings schema.

## Installation

Download the installer for your platform from the
[releases page](https://github.com/perryclarke/clipsync/releases). Install
the same release everywhere: the clients are versioned together, though
what actually decides compatibility is the protocol's `caps` field rather
than the version number.

**macOS 15+.** Open `ClipSync.dmg` and drag ClipSync to Applications. The
build is signed with an Apple Development certificate but not notarized,
so the first launch needs right-click then *Open* rather than a
double-click. Leave it in /Applications; the "Open ClipSync at login"
toggle needs it there to work.

**Windows 11 25H2+.** Run `ClipSync.msi`. It installs per user and
upgrades an existing 0.6.x or 0.7.x in place, closing and relaunching a
running ClipSync by itself. It is signed with a self-signed certificate
that Windows will not validate, so expect an unknown-publisher warning;
the MSI installs regardless. The matching public certificate is not a
release asset (`build-msi.ps1` writes it to `dist/clipsync-codesign.cer`
when you build), so making the signature validate means building the MSI
yourself and importing that file into *Trusted Root Certification
Authorities* and *Trusted Publishers*.

**Ubuntu 26.04+.** Install with `sudo apt install
./clipsync_0.8.0_amd64.deb` rather than `dpkg -i`, so the dependencies
resolve. Files land in `/opt/clipsync` with a `/usr/bin/clipsync`
symlink, alongside a `systemd --user` unit and an autostart entry, and
the daemon starts at your next login; `systemctl --user daemon-reload &&
systemctl --user start clipsync` starts it without logging out. The tray
icon additionally needs a StatusNotifierItem host, which on GNOME means
the AppIndicator extension (`gnome-extensions enable
ubuntu-appindicators@ubuntu.com`); without one the daemon syncs normally
and simply has no icon. Only amd64 is published: `build-deb.sh arm64`
produces an arm64 package, but it has never been run on arm64 hardware.
See *Platform differences on Linux* for the three ways Linux behaves
differently from the other two.

**Then pair them.** Trust is two-sided. On each machine, open ClipSync
and click **Trust** on the other; the first side's connection keeps
failing until the second side trusts too. After that both remember each
other and reconnect on their own.

To build from source instead, see *Building* below.

## Features

All three clients behave the same way; only the UI chrome differs, plus
the Linux exceptions noted under *Platform differences on Linux*.

### Pause / resume

Sending can be paused from the tray / menu-bar menu. **Pause Syncing**
stops this device sending anything to anybody; the title reads
"ClipSync — Paused" (and on Windows the tray tooltip follows), so it is
visible without opening the menu. Each known peer also has its own
pause — a ⏸ / ▶ button on Windows, an entry in the peer's submenu on
macOS, a switch on the device's row in the ClipSync window on Linux —
which pauses sending to just that machine. Both are send-only:
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

*Linux:* ClipSync window → **Excluded apps**. There is no app picker;
apps are matched by X11 `WM_CLASS`, typed in directly (find one with
`xprop WM_CLASS` and a click on the window). See *Platform differences
on Linux* for what this can and cannot match.

## Repository map

- **PROTOCOL.md** — wire format and state machines (source of truth for
  every implementation).
- **HANDOFF.md** — design decisions, what has been verified on each
  platform, and where the implementation deviates from the original plan
  (notably the trust flow — see *Known gaps*).
- **clipsync-mac/** — Swift 6 / SwiftUI `MenuBarExtra` app.
- **clipsync-win/** — .NET 10 / WinUI 3 tray app.
- **clipsync-linux/** — .NET 10 daemon with a StatusNotifierItem tray. Reuses
  the Windows protocol layer by linking those source files directly, rather
  than keeping a third copy of the codec and transport.
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

**Linux.** Run `clipsync-linux/build-deb.sh` (`amd64` or `arm64`). It
publishes a self-contained build and packages it as
`dist/clipsync_<version>_<arch>.deb`, with a `systemd --user` unit, an
autostart entry, and `/usr/bin/clipsync`. For development,
`dotnet run --project clipsync-linux` gives a console harness with the same
daemon behind it (`peers`, `trust <did>`, `pause`, `status`; `--self-test`
exercises the clipboard round trip). Requires `avahi-daemon` and an X or
XWayland display.

**Running it from a shell.** `clipsync` starts the daemon in the
background and hands the prompt straight back, printing the pid and where
it is logging. A second `clipsync` does not start a rival daemon: it opens
the running one's window and exits. `clipsync --foreground` (`-f`) instead
runs it right there, with the console harness on stdin. The background
copy is chosen only when a terminal is actually attached, so the systemd
unit and the autostart entry, which have none, keep running the daemon in
the process they started.

The tray icon needs a StatusNotifierItem host. On GNOME that is the
AppIndicator extension — `gnome-extensions enable
ubuntu-appindicators@ubuntu.com`. Without one the daemon syncs normally and
simply has no icon, and says so at startup.

The UI is the ClipSync window (GTK4 / libadwaita): the device list with
**Trust** buttons for new peers, a per-device send switch, hide/show,
and the global **Pause Syncing** switch. Clicking the icon shows a
three-item menu — **Settings…**, Pause, Quit — and double-click or
middle-click opens the window directly. (GNOME's AppIndicator extension
never routes a single click to the window while a menu is attached, and
a StatusNotifierItem menu can only be a flat list that closes on every
click, which is why the menu stays minimal and the window does the
work.) Closing the window leaves the daemon running. The icon itself
matches the other platforms — the clipboard glyph with a blue wifi badge,
swapping to orange pause bars while paused.

The window's **General** group holds "Start ClipSync when you sign in"
(an XDG autostart entry; the .deb enables it system-wide, and the switch
writes a per-user override) and **Start over**, which forgets every
trusted device, hidden device, excluded app and paused peer, then
restarts the daemon — same wording and behaviour as the other two
platforms.

**Debug logging (Linux).** Off by default. `--debug` on the command line or
`CLIPSYNC_DEBUG=1` in the environment. Output goes to stderr, which as a
service means `journalctl --user -u clipsync`. A daemon backgrounded from
a shell has no stderr to read, so it writes to
`~/.local/share/ClipSync/clipsync.log` instead — truncated at each launch,
since the journal already keeps the history for the service.

**Debug logging (Windows).** Off by default. Turn it on with `--debug`
(also `-d` / `/debug`) on the command line, `CLIPSYNC_DEBUG=1` in the
environment, or an empty `debug-enabled` file next to the log. Output
goes to `%LOCALAPPDATA%\ClipSync\debug.log`.

## Platform differences on Linux

Everything in *Features* works on Linux, with three documented exceptions.

**No clipboard history.** Linux has no equivalent of macOS 15's Clipboard
History or Win+V, so items land in the active clipboard only. Not a gap to
work around — there is nothing to land in.

**Excluded apps cover X11/XWayland applications only.** Wayland gives a
background process no way to learn which application made a copy: the
selection arrives owned by the compositor's bridge. Where the owner is a
real X window it is identified and matched (by `WM_CLASS`); where it is not,
the item is treated as not excluded and is synced — the same fail-open
behaviour the other two platforms document for apps they cannot identify.
It just happens more often here.

**The PRIMARY selection is not synced.** Middle-click selection fires on
every drag, has no counterpart on macOS or Windows, and syncing it would
broadcast continuously. Only CLIPBOARD is read and written.

Also worth knowing: on GNOME the clipboard is reached through X11/XFixes
over XWayland, because Mutter advertises no `ext-data-control-v1`. See
`HANDOFF.md` for the measurement and when to revisit.

## Known gaps

Discovery, mTLS transport, CBOR framing, clipboard watchers and writers
with loop suppression, trust-store persistence, pause/resume, excluded
apps, and the menu-bar / tray UI are in place and internally consistent
across all three codebases.

Trust is two-sided TOFU (see `HANDOFF.md`, *Trust flow*): a newly
discovered peer shows as untrusted with a **Trust** button; clicking it
on both sides pins the peer's key, and once a Hello completes both sides
promote it to the persistent trust store, so future launches reconnect
without clicks. That is the intended design for a LAN-only personal
tool.

Items are synced whole up to **100 MiB**: formats up to 64 KiB travel
inline, larger ones (images, big text) are streamed in 1 MiB chunks and
reassembled on the other side before anything touches the clipboard. If
an item is over the cap, the sender keeps the formats that fit (in
clipboard order) and drops the rest, logging each drop; a copy where
nothing fits is not synced. There is no prompt or per-item consent — the
cap exists precisely because a copy is sent immediately, with no way to
wait for the receiver to want it. Peers older than 0.7 do not advertise
the `stream` capability and receive only the ≤ 64 KiB formats.

Still open:

1. **File contents are not transferred.** A copied file arrives as its
   path only (`application/x-file-url`), which the other machine cannot
   use; the writers skip it. Sending file bytes (destination folder,
   many files, name collisions) is a separate piece of work that would
   reuse the same chunk stream.

The macOS self-signed `SecIdentity` builder (`Identity.swift`
`createKeychainIdentity()`) and its Windows counterpart
(`Identity.cs` `CreateSelfSignedCert()`) are **complete** — earlier
revisions of this list described them as stubs; they no longer are.

## Disclosure
While I have 40 years experience with C, 30 with C++ and 20 with Objective-C, I have not
written anything substantial with either C# or Swift which is what this application is 
implemented in. My role here was closer to product manager: I designed the application 
and communicated the requirements to the "programming team".  Or, put another
way, this was vibe coded.
