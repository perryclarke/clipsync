# ClipSync — Handoff to Windows

This doc captures the state of ClipSync as of the mac build getting Mac↔Mac sync working, for picking up the Windows implementation in a new Claude Code conversation.

## Read these first

1. `PROTOCOL.md` — wire protocol, source of truth for both platforms.
2. `clipsync-mac/Sources/ClipSync/` — reference implementation. Mirror the file structure on Windows.
3. `clipsync-win/ClipSync/` — Windows skeleton already exists (generated earlier, not yet built/tested).

## Current state

### macOS (`clipsync-mac/`) — WORKING

Menubar app (`LSUIElement=true`) that:
- Generates a persistent identity: Ed25519 + P-256 TLS key in Keychain, cert DER stashed as a generic-password item. `did = SHA256(SPKI of P-256 pubkey)`, shown as 8-hex prefix.
- Advertises `_clipsync._tcp` via `NWListener` with TXT `{v, did, name, caps, pend}`.
- Browses with `NWBrowser(for: .bonjourWithTXTRecord(type:...))` — the `WithTXTRecord` variant is required to get `did` from the TXT.
- Paired peers connect over mTLS 1.3 with SPKI pinning via `sec_protocol_options_set_verify_block`.
- Length-prefixed CBOR framing (4-byte BE length, 16 MiB max).
- Clipboard watcher polls `NSPasteboard.changeCount` at 200ms.
- Writes remote items to local pasteboard; macOS 15 Clipboard History picks them up automatically.
- Built via `swift build -c release`, then manually assembled into `ClipSync.app` (Info.plist at bundle root, binary at `Contents/MacOS/ClipSync`, `AppIcon.icns` at `Contents/Resources/`). Ad-hoc signed with `codesign --sign -` (no Developer ID needed for personal use).
- `reset-identity.sh` at `Contents/Resources/` wipes all local state for testing.

### Trust flow (deviated from plan)

The plan called for SPAKE2 PIN enrollment. We landed on **two-sided TOFU** instead, which is simpler and fine for a personal-use LAN tool:

1. Both devices advertise with `pend=1` when their trust store is empty.
2. Each side sees the other as "Not trusted" in the menubar with a Trust button.
3. Clicking Trust on side A adds B to A's persistent trust store and attempts mTLS.
4. A's first connect fails because A isn't in B's trust store yet.
5. When the user clicks Trust on B, B adds A and tries to connect. mTLS succeeds (A already trusts B).
6. After Hello completes, both sides auto-promote the peer into persistent trust via `PeerRegistry.onPeerConnected` → `TrustStore.add`. Future launches reconnect without any clicks.

SPAKE2 can be added later if we ever want stronger enrollment. TOFU is ample for the threat model (LAN-only, personal devices, one-time pairing).

### Known issues / backlog

- `LargeItemOffer`/`LargeItemAccept` flow for ≥100 MB is in the protocol but not implemented on either side.
- Keychain cert persistence on ad-hoc signed apps was flaky (`-34018 errSecMissingEntitlement`) — worked around by storing the cert DER in a `kSecClassGenericPassword` item and rebuilding `SecCertificate` on load. `SecKeyCreateRandomKey(kSecAttrIsPermanent: true)` avoids the entitlement requirement for the private key.
- The Windows skeleton has never been compiled.

## Windows implementation plan

Build order:

1. **Compile the skeleton.** `clipsync-win/ClipSync.sln` — open in Visual Studio, make sure `net9.0-windows10.0.22621.0` + Windows App SDK 1.6+ references resolve. Fix any issues so it launches as a tray app with no functionality.
2. **Identity** (`Security/Identity.cs`). Ed25519 via BouncyCastle. P-256 + self-signed cert via `System.Security.Cryptography.X509Certificates`. DPAPI-protect the private key file in `%LOCALAPPDATA%\ClipSync\`. `did = SHA256(SPKI)` same as mac — this is the interop guarantee.
3. **TrustStore** (`Security/TrustStore.cs`). JSON file in `%LOCALAPPDATA%\ClipSync\trust.json` keyed by didHex.
4. **Discovery** (`Net/Discovery.cs`). **Makaretu.Dns** for mDNS over IPv6 — advertise + browse `_clipsync._tcp`. TXT record must include `did`, `name`, `pend`. If the NuGet package doesn't resolve IPv6 link-local properly, fall back to P/Invoke `dnsapi.dll` `DnsServiceRegister`/`DnsServiceBrowse` (Win10 2004+).
5. **Transport** (`Net/PeerConnection.cs`). `TcpListener`/`TcpClient` bound to `IPAddress.IPv6Any`, wrapped in `SslStream` with `SslClientAuthenticationOptions` / `SslServerAuthenticationOptions`. `RemoteCertificateValidationCallback` pins on SPKI hash against TrustStore — this is the mac's verify_block equivalent. Require client certs on server side.
6. **CBOR codec** (`Net/Protocol.cs`). **PeterO.Cbor** NuGet. Schema must match `PROTOCOL.md` exactly — integer type tags, `t` discriminator, `did` as byte-string not hex. Test a round-trip against the mac-encoded frames as golden vectors.
7. **Clipboard** (`Clipboard/ClipboardWatcher.cs`, `ClipboardWriter.cs`). `Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged` event — no polling needed. `Clipboard.SetContent(pkg)` + `Clipboard.Flush()` so data persists after app exits. Win+V history records inbound writes automatically if the user has Clipboard History enabled in Settings.
8. **UI** (`UI/TrayIcon.cs`). `H.NotifyIcon.WinUI` NuGet. Context menu with peer list and Trust button, mirroring the mac menubar.

## Cross-platform interop checklist

- [ ] did = SHA256 of SPKI DER bytes — identical algorithm both sides.
- [ ] Service type `_clipsync._tcp` — no trailing dot in advertisement, no `local.` suffix (both stacks add it).
- [ ] TXT record keys: lowercase, UTF-8 values. `did` is hex string.
- [ ] CBOR frame: 4-byte big-endian length prefix, body is CBOR map with integer `t` field.
- [ ] TLS 1.3 only, mutual auth required.
- [ ] TOFU: treat peers advertising pend=1 as pending; click Trust on both sides; auto-promote on successful Hello.
- [ ] Loop prevention: when writing a remote item to the local clipboard, stamp its content hash in a short-lived set and skip the next local change that matches.

## Testing

Once Windows builds and discovers:

1. Mac ↔ Win plain text.
2. Mac ↔ Win small image (PNG via clipboard).
3. Mac ↔ Win file (single, then multi-select).
4. Trust rejection: launch on a third device, verify it sees neither peer until Trust is clicked on both sides.
5. Restart both apps — should auto-reconnect without re-trusting.
6. Clipboard History: on Windows, verify inbound items land in Win+V.

## Environment specifics

- Mac dev machines: **BetaMacBook** and **Kodachrome**, both running macOS 15.
- Mac Swift toolchain: `/Applications/Xcode-26.1.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/swift`.
- Apple Developer cert is downloaded but not used — ad-hoc signing is sufficient for personal use. Developer ID + notarization is a separate future step if distributing.
- Windows target: Windows 11 25H2+, .NET 9, Windows App SDK 1.6+, WinUI 3.

## Handoff 2026-08-17 — large-item streaming (for the Mac)

Branch `large-item-streaming` (PR against `main`). Windows side is built,
unit-tested (73/73), and running; the **macOS side is written but has not
been compiled** — there is no Swift toolchain on the Windows box. Design:
`docs/superpowers/specs/2026-08-17-large-item-streaming-design.md`.

### What changed on the Mac

- `Sources/ClipSync/Net/StreamPlanner.swift` (new) — pure: 100 MiB item
  cap (drop formats in order, log), 64 KiB inline threshold, `stream`
  capability gate, per-connection stream ids.
- `Sources/ClipSync/Net/StreamAssembler.swift` (new) — pure state machine:
  park / chunk / end / materialise; one pending item per connection, 30 s
  idle drop, rejects over-cap declared sizes, gaps, overruns, bad hashes.
- `Sources/ClipSync/Net/Protocol.swift` — `encodeFileChunk/End`,
  `decodeFileChunk/End`, `decodeHelloCaps`.
- `Sources/ClipSync/Net/PeerConnection.swift` — advertises `stream` in
  Hello (`localCaps`), records `peerCaps`, `send(item:)` now plans and
  emits item + chunks + end, `handle` feeds the assembler on
  `.clipboardItem` / `.fileChunk` / `.fileEnd`, keepalive timer drops a
  stale pending item. `import Crypto` added (SHA-256 of streams).
- Tests: `StreamPlannerTests`, `StreamAssemblerTests` (run in the normal
  suite) and `StreamLoopbackTests` (opt-in, like `PeerCertBindingTests`).

Nothing outside `Net/` was touched: watcher, writer, registry, pause,
menu are as before. `PROTOCOL.md` §6.1/6.2/6.3–6.5/10 and README updated.

### Done on the Mac (2026-08-18)

The Mac side was compiled, tested, and run on `large-item-streaming`.

1. `swift build` — **compiled clean, no Swift fixes were needed**; the
   C#-mirrored logic built as written.
2. `swift test` — **75 pass, 0 failures** (planner + assembler in the
   normal suite). `CLIPSYNC_TLS_LOOPBACK=1 swift test --filter
   StreamLoopback` — **passes**: a 5 MB item through two real
   `PeerConnection`s over mTLS, materialised back to inline
   (`streaming 5.0 MB as stream 1` → `streamed item complete (2 formats)`).
3. Rebuilt the app, signed with the Apple Development cert, ran it against
   the LAN. It connected to Windows peer `PCLARKE-WIN11-L` over mTLS with a
   clean Hello. **That box is still on 0.6.x (no `stream` cap)**, so a
   copied screenshot logged the expected degradation —
   `dropping image/png (0.4 MB): peer lacks stream capability` — confirming
   the capability gate against a real non-stream peer.

Both branches of the send decision are therefore verified: streaming (via
the loopback test) and safe drop-to-a-0.6.x-peer (live).

### Still to verify (needs a Windows box on this branch)

- End-to-end Mac ↔ Win streaming: the LAN Windows peer is 0.6.x, so the
  live `stream`-to-`stream` path could not be exercised. Update Windows to
  this branch, then: Retina screenshot Mac → Windows (Windows shows the
  PNG); >64 KiB text Windows → Mac; something over 100 MB for the drop
  line. The `peer lacks stream capability` line disappearing is the first
  sign both sides speak `stream`. Windows log: `--debug`,
  `%LOCALAPPDATA%\ClipSync\debug.log`; Mac: run the binary directly and
  read stderr (`open`-launched, NSLog does not surface via `log stream`).

### Notes / gotchas

- The whole stream is enqueued on `NWConnection.send` at once (up to 100
  frames of 1 MiB). NW buffers in memory; the data is already in memory,
  so this is fine, but a second copy during a long Wi-Fi transfer queues
  behind it. Cancellation was consciously left out (spec).
- `StreamAssembler` runs on the connection's queue (`.main`, same as the
  receive handler and keepalive `Timer`); no locking.
- Files still travel as paths only — out of scope, recorded in README.
- Bump to 0.7.0 on both platforms when this ships; the `stream` cap is
  the compatibility signal, not the version.

## Handoff 2026-08-21 — 0.8.0 quality-of-life features (for the Mac)

Windows implemented four features for 0.8.0 (both platforms already bumped
to 0.8.0 / build 10 in this commit). None of them touch the wire protocol;
the Mac needs its own equivalents of the first three:

1. **Hidden devices.** An untrusted (pending) peer row gets a small round
   slashed-eye button (SF Symbol `eye.slash` on the Mac) beside Trust; clicking it hides that device from the peer
   list. Hidden devices live in the settings file (`hiddenPeers`, a list
   of `{did, name}` — see `clipsync-win/ClipSync.Core/Settings/
   AppSettings.cs`, the reference implementation, and its tests) and can
   be unhidden from a "Hidden devices" list in Settings. Hiding is a
   display preference only: it does not touch trust, discovery or
   connections; the popup just filters those DIDs out. Purpose: an office
   subnet where dozens of strangers' machines would otherwise fill the
   list. Please match the JSON shape exactly — the settings schema is
   shared.

2. **Start over.** A Settings card ("Start over", confirmation dialog)
   that clears the trust store and every preference (excluded apps,
   per-peer pauses, hidden devices), then relaunches the app — returning
   it to first-run state. The device identity is deliberately kept, and
   peers are not told; both sides re-trust to reconnect. On Windows this
   is `TrustStore.Clear()` + `AppSettings.ResetAll()` + restart.

3. **Start at login.** Windows: a "Start ClipSync when you sign in"
   toggle over the HKCU Run key the MSI seeds. Mac equivalent:
   `SMAppService.mainApp` register/unregister behind a "Open at login"
   toggle in Settings.

4. *(Windows-only, no Mac work)* the tray popup already filters hidden
   devices and shows the slashed-eye on pending rows.

Gotchas:
- `AppSettings` hidden entries normalise DIDs to lowercase and fall back
  to the first 8 hex chars when a name is blank; hide is idempotent and
  keeps the first name. Tests in `ClipSync.Tests/AppSettingsTests.cs`
  cover round-trip, idempotency, blank names and ResetAll.
- Windows named things: "Start ClipSync when you sign in", "Hidden
  devices", "Start over" — keep the Mac wording parallel ("Open ClipSync
  at login" is the natural Mac phrasing for the first).

**Update, same day:** the Mac equivalents are now written (from Windows,
NOT compiled — no Swift toolchain there): `hiddenPeers` in
AppSettings.swift with tests, hide/unhide + visiblePeers filtering in
AppCoordinator/MenuBarView (SF Symbol `eye.slash` on pending rows),
"Hidden devices" + "Start over" + "Open ClipSync at login"
(SMAppService.mainApp) in SettingsWindow.swift. To do on the Mac:
`swift build`, `swift test`, then eyeball the settings window and the
hide/unhide round trip. The login toggle only works from a real app
bundle; under `swift run` it is disabled by `canOpenAtLogin`.

## Handoff 2026-08-22 — clipsync-linux, Phase 0 findings

Planning and spike work for a third client (Ubuntu 26.04, GNOME/Wayland).
No code in `clipsync-mac/` or `clipsync-win/` was touched; the Linux
client is to reuse the portable C# protocol layer by *linking* those
sources, not by refactoring them. Recorded here because three of these
findings would silently break any third implementation.

### `did` is not what PROTOCOL.md §2 says it is

The spec says the device id is SHA-256 of the **SPKI** of an **Ed25519**
certificate. Both shipping clients instead hash the **raw X9.63
uncompressed EC point of the P-256 TLS key** (65 bytes):

- `clipsync-mac/Sources/ClipSync/Security/TrustStore.swift` `spkiSha256(of:)`
  hashes `SecKeyCopyExternalRepresentation`, and says in a comment that
  this is "not strictly the SPKI hash".
- `clipsync-win/ClipSync/Security/Identity.cs` `ComputeDid` hashes
  `cert.PublicKey.EncodedKeyValue.RawData`, deliberately matching it.

The Ed25519 key exists on both sides but is a *logical* identity only —
TLS and pinning are P-256 end to end. `PROTOCOL.md` is deliberately left
unchanged (the wire format is frozen); this is the erratum. **A new
implementation must match the code, not §2/§4.** A DID golden-vector test
is the first thing to write on any new platform.

Two smaller divergences from the spec, both harmless but worth knowing:
Windows advertises `caps=text,image,files,rich` in its mDNS TXT while the
Mac advertises `...,rich,stream` (capability gating is on the `Hello`
`caps`, never the TXT), and neither side percent-encodes the TXT `name`
that §3 requires.

### ext-data-control-v1 is absent from Mutter (GNOME Shell 50.1)

Measured on Ubuntu 26.04, `XDG_SESSION_TYPE=wayland`, by dumping the
registry a real client sees. The compositor advertises **no data-control
global of any kind** — no `ext_data_control_manager_v1`, no
`zwlr_data_control_manager_v1`. The only selection interfaces are
`wl_data_device_manager` v3 and `zwp_primary_selection_device_manager_v1`,
both focus-gated and so unusable from a background daemon. `wl-paste`
does not fail on this desktop, it *hangs*, which is the same finding from
the user's side.

X11/XFixes over XWayland is therefore the **primary** clipboard backend on
GNOME, with ext-data-control kept as a backend that auto-selects wherever
the global does appear (wlroots compositors, and GNOME if Mutter ever
ships it). This inverts the priority originally intended, for a measured
reason. **Revisit when Mutter ships ext-data-control**; the probe already
prefers it and will switch over with no code change.

### What the X11 path actually does (measured, not assumed)

- XFixes `SetSelectionOwnerNotify` gives clipboard-change notification
  from a background process with no focus and no polling.
- Multi-format read works: a real GTK client offered 6 targets
  (`UTF8_STRING`, `COMPOUND_TEXT`, `TEXT`, `STRING`, `text/plain`,
  `text/plain;charset=utf-8`), all retrieved with correct sizes.
- **No practical size ceiling below the app's own cap.** A 100 MiB item
  round-tripped through the X selection/INCR path byte-identical in 1.1 s
  (16 MiB in 175 ms, 64 MiB in 694 ms). The 100 MiB cap in §10 does not
  need lowering on Linux.
- **Loop suppression will work.** Data written and read back is
  byte-identical, so the existing `SuppressionPolicy` SHA-256 over
  canonical bytes matches. Only one change event is emitted per write —
  the bridge does not re-announce in a loop. Note that when a selection
  owner exits, something else (clipboard-manager persistence) takes
  ownership and re-announces the *same bytes*, which the hash also
  covers as long as it is still inside the TTL.

### Source identification — better than expected, but not universal

Reading `WM_CLASS` off the selection owner does **not** work, even for
X11 apps: toolkits own selections with a dedicated unmapped window that
carries no `WM_CLASS`. It can be recovered, though — X partitions
resource IDs per client, so the owner's XID high bits identify the
client, and a managed toplevel from `_NET_CLIENT_LIST` with the same
client base does carry `WM_CLASS`. Verified end to end (owner `0xc00002`
→ toplevel `0xc00004` → `python3`).

This makes excluded-apps viable for X11/XWayland apps. It remains
impossible for Wayland-native apps, whose selections are owned by the
bridge — those fall into the "cannot identify the foreground app, so
treat as not excluded and sync it" case the other two platforms already
document. The Linux UI must say so explicitly rather than implying full
coverage.

### Chosen stack

XCB rather than Xlib, deliberately: Xlib's fatal IO-error path ends in
`exit(1)` and can only be escaped by longjmp-ing out of a callback, which
a managed daemon cannot do safely. XCB reports the same condition through
`xcb_connection_has_error()`, so the daemon catches it and rebuilds the
connection. Verified: against a dead display the watcher retries once a
second indefinitely instead of aborting.

Discovery on Linux will use the Avahi D-Bus API rather than an in-process
mDNS responder — `avahi-daemon` already owns port 5353, and two
responders on one host collide.

### Confirmed: Wayland-origin copies do reach an X11 watcher

The load-bearing question, answered by hand on 2026-08-22. Copying text
and then an image in Wayland-native GNOME apps produced XFixes events on
the X side both times, with full data. Sending is therefore viable, not
just receiving. (Receiving was measured separately: 8 MiB crossed Mutter's
bridge into a focused GTK Wayland client byte-identical in 666 ms.)

Both events reported the selection owner as the same bridge window, so
**every Wayland-origin copy is `<unidentifiable>` for source purposes** —
confirming by measurement what was expected in the design: excluded-apps
matches X11/XWayland apps only.

The target list a real Wayland image copy produces (from Firefox) is much
messier than the mac or Windows clipboard, and the Linux watcher has to
clean it up before it reaches `StreamPlanner`:

- **Empty formats are offered.** `image/tiff`, `image/x-icon`,
  `image/vnd.microsoft.icon`, `text/_moz_htmlcontext` and six more all
  appeared in `TARGETS` and returned **0 bytes**. Drop zero-length formats
  rather than sending peers a pile of empty MIME types to write.
- **Aliases duplicate the same bytes.** `image/bmp`, `image/x-bmp` and
  `image/x-MS-bmp` were all 576,054 bytes with identical SHA-256. So were
  the four text targets. Dedupe by content hash and keep the canonical
  name, or one screenshot ships several times over the wire.
- **At least one format is mislabelled.** `audio/x-riff` and `image/webp`
  had identical size and hash — WebP is a RIFF container, and something in
  the chain offers it under an audio type. Do not trust the target name as
  a content type.

One Firefox image copy offered 20 targets totalling ~1.4 MB, of which the
distinct content was one PNG (254 KiB), one JPEG (23 KiB), one BMP
(576 KiB), one WebP (14 KiB) and a little HTML. Reading and sending all of
it verbatim would be wasteful; the watcher should filter to formats worth
syncing rather than mirroring the X target list.

Still unverified: XWayland restart resilience against a real XWayland
(the reconnect loop is verified against a dead display, but a live
mid-session restart has not been exercised).

### clipsync-linux skeleton — status 2026-08-22

`clipsync-linux/` builds and its 17 tests pass. What exists: the linked
protocol layer, `Identity`, `TrustStore`, the `ISecretStore` seam with a
`FilePermissionStore` backend, and a `Program.Main` that prints the device
id. Discovery, transport wiring, clipboard and UI are still to come.

**The linking strategy works.** `ClipSync.Linux.csproj` compiles eleven
files straight out of `clipsync-win` — `Protocol.cs`, `PeerConnection.cs`,
`PeerRegistry.cs`, `StreamPlanner`, `StreamAssembler`, `ClipboardItem`,
`AppSettings`, `AppIdentity`, `SyncPause`, `SuppressionPolicy`,
`ForegroundRing`, plus Core's `Log` seam — with **no edits to that tree**.
The Linux `Identity` and `TrustStore` sit in the same `ClipSync.Security`
namespace with the same public surface, so the linked files compile
against them unchanged. That the whole mTLS/CBOR/streaming layer builds
on Linux was worth proving at skeleton time rather than at step 3.

Gotchas worth knowing:

- **Do not annotate `Identity`/`TrustStore` with `[SupportedOSPlatform]`.**
  It looks right and it poisons every linked call site with CA1416, since
  the callers come from a tree that is not Linux-annotated. Declare the
  project Linux-only instead (`<SupportedPlatform Remove/Include>` in the
  csproj), which is the honest statement and silences it properly.
- The test project lives under `clipsync-linux/`, so the app's default
  source glob picks it up unless explicitly removed. It is.
- Certificates are re-imported through PKCS#12 on load. On Linux
  `SslStream` needs the private key attached in that form for TLS 1.3
  client auth; a cert without it fails the handshake uninformatively.
- Key material and trust live in `~/.local/share/ClipSync/` at 0600,
  alongside the `settings.json` the linked `AppSettings` writes — that
  directory is what `LocalApplicationData` resolves to here, so all three
  agree without any path plumbing.

The **did golden vector** is `ClipSync.Linux.Tests/Fixtures/golden.p12`
with an expected id of
`557d95a190560457215a3045e607167dd326a1bb83f16ec5d42869490e0baa79`. That
value was produced *outside* .NET — the public point extracted with
`openssl ec -pubin -text` and hashed with coreutils `sha256sum` — so the
test checks .NET against an independent toolchain rather than against
itself. A second test rebuilds the point from the curve parameters, and a
third asserts the id is **not** the SPKI hash, so that anyone "fixing"
`ComputeDid` to match PROTOCOL.md §2 breaks a test that explains why.

Verified by hand: the id is stable across restarts, `--reset` clears trust
while keeping the identity, and the key files are `-rw-------`.

### clipsync-linux discovery — status and interop result 2026-08-22

Discovery is implemented over the Avahi D-Bus API (`Platform/Backends/
AvahiDBus.cs` behind `IDiscoveryBackend`, driven by `Net/Discovery.cs`) and
works against the real LAN. It found all three live peers — `Perry's Mac
Studio`, `Kodachrome`, `Kadobe M1 MBP8` — within 50 ms of startup, all
correctly `Pending`, with duplicate per-interface resolves deduped.

The Linux `Discovery` is a fraction of the size of the Windows one because
avahi-daemon does the work: no interface pinning, no forcing multicast
egress, no re-announce burst. Those exist on Windows to work around
Makaretu; they are Avahi's job here.

**The did golden vector is confirmed against a real Mac.** Dialling
Kodachrome produced `ValidatePeer: peer did=b6bf89d9…, trusted=True` — the
id computed from its actual TLS certificate matched the id it advertised
in mDNS. That is the erratum above proven end to end, not just in a unit
test: hashing the SPKI instead would have produced a mismatch here.

The connection then fails with `IOException: The decryption operation
failed` immediately after `TLS handshake complete, role=Client`. That is
the expected TOFU half-state, not a bug: in TLS 1.3 the client finishes
the handshake before the server has validated the client certificate, so
a peer that does not trust us yet rejects it afterwards and the alert
surfaces on the first read. It is step 4 of the trust flow above —
"A's first connect fails because A isn't in B's trust store yet".
Completing it needs Trust clicked on the Mac for `dekatron` /
`fdbc630e`. Until then the maintenance loop retries every 15 s, which the
log shows working.

Two Tmds.DBus traps, both of which fail *silently* and cost real time:

- **`Notification<T>.Exception` throws unless the notification is a
  completion.** Writing the obvious `if (n.Exception is { } e)` as the
  first line of a signal handler throws on the very first signal, inside
  the observer callback, which tears down the subscription and surfaces
  nothing anywhere. Symptom: browsing starts cleanly and no peer is ever
  reported. Check `HasValue` first, and only read `Exception` when
  `IsCompletion`.
- **Subscribe before creating the browser.** Avahi emits `ItemNew` for
  every service it already knows the instant `ServiceBrowserNew` returns.
  Creating the browser first and then subscribing — the natural order,
  since that call returns the path you would match on — loses the entire
  initial burst, so peers already on the network stay invisible while
  newly announced ones appear normally. Match on a null path instead.

Also worth knowing: `MessageWriter` is a ref struct, so it cannot be held
across an `await`; messages have to be built in a synchronous helper and
sent afterwards.

**Update, same day — the handshake completes.** With Trust clicked on
Kodachrome, Linux ↔ macOS connects over mTLS 1.3 and exchanges Hello:
`Hello from: name=Kodachrome, ver=0.8.0, did=b6bf89d9…`, peer shown
`Online v0.8.0`. On restart it reconnects with no clicks, one second from
launch. The whole linked layer — TLS 1.3 client auth with the
PKCS#12-loaded certificate, SPKI pinning, CBOR framing, Hello decode —
works unmodified on Linux.

Simultaneous connect was exercised for real and the linked registry
handles it correctly: our client connection came up at 21:50:33.729, the
Mac's dial landed on our listener at 21:50:34.091, and the registry logged
`replacing duplicate connection` leaving exactly one live connection.
Kodachrome's did (`b6bf89…`) sorts below ours (`fdbc63…`), so its client
connection wins — which is our server side, matching the documented rule.

**The listener must stay dual-stack.** The Mac dialled us at
`[::ffff:10.0.0.156]` — an IPv4-mapped address — even though both hosts
have IPv6 and PROTOCOL.md §3 specifies AAAA only. Binding IPv6Any with
`IPv6Only = false` (as the Windows client also does) is what makes that
work; a strict IPv6-only listener would have refused the Mac's connection
and the two would never have paired.

### clipsync-linux — clipboard, tray and packaging, 2026-08-22

The client is now feature-complete against the parity checklist: discovery,
mTLS transport, clipboard watcher and writer with loop suppression, global
and per-peer pause, excluded apps, the 100 MiB cap and stream capability
(all inherited from the linked layer), a StatusNotifierItem tray, and a
`.deb`. 52 unit tests pass, plus a `--self-test` that needs a real X server.

**Format hygiene is a Linux-only layer.** The watcher does not mirror the X
target list; it asks for a fixed set of canonical formats — the same
`text/plain;charset=utf-8`, `text/html`, `image/png`,
`application/x-file-url` the Windows client produces — and takes the first
source target that yields bytes for each. That excludes the empty formats,
the aliases and the mislabelled `audio/x-riff` by construction rather than
filtering them afterwards. `Clipboard/ClipboardFormats.cs` holds the table.

**Two bugs the self-test caught, both invisible to unit tests:**

- *INCR deadlocked on our own write.* The watcher re-read the clipboard on
  the same connection that owned it, so an INCR transfer needed the X thread
  to answer chunk requests it was itself blocked waiting for. Anything over
  64 KiB stalled for the full 30 s timeout, and while it stalled no other
  client was served either. Fixed by not re-reading our own selection at all
  (`owner == our window`), which is also simply correct: the writer already
  stamped the content.
- *A Delete arriving mid-read was dropped.* Inside the INCR read loop a
  `PropertyNotify(Delete)` on another window is a requestor asking for its
  next chunk. Discarding it stalled any transfer we were serving. Both read
  loops now dispatch it.

**Loop suppression needed a second stamp.** The writer drops formats the
clipboard cannot hold (a file path), so what comes back is a *smaller* item
with a different hash than the one that arrived. Stamping only the incoming
item's hash would let our own write echo back to the peer. Both the original
and the reduced form are stamped.

**Excluded apps use `AppKind.Exe` keyed on `WM_CLASS`.** There is no
`AppKind` for an X window class and adding one would mean editing
`AppIdentity`, which is linked from `clipsync-win`. `Exe` normalisation
(lowercase, take the file-name part) leaves a `WM_CLASS` untouched, so the
settings file stays schema-compatible and the other two clients read these
entries and simply never match them — correct, since they name Linux apps.

**The tray is D-Bus only, no toolkit.** An SNI entry is two objects plus a
registration call; pulling in GTK to draw what the shell draws itself would
add a large dependency and a second main loop. `Ui/TrayIcon.cs` serves
`org.kde.StatusNotifierItem` and `com.canonical.dbusmenu`.

Two things to know if you touch it:

- **Register the handler at `/`, not at `/StatusNotifierItem`.** The menu
  lives at `/MenuBar`, a *sibling* rather than a child, so a handler rooted
  at the item path never sees it and every menu call returns "no such
  method" — including the property reads, which is what it looks like first.
- `MessageWriter` is a ref struct, so it cannot be captured by a lambda or
  local function; the dbusmenu property dictionary is written longhand for
  that reason. Nested variant-structs are written as an explicit signature
  followed by the struct, since `VariantValue` has no struct factory.

**No SNI host is running on this machine.** `ubuntu-appindicators` is
installed but INACTIVE, and no GNOME extensions are enabled at all, so
`org.kde.StatusNotifierWatcher` is not on the bus. The tray was therefore
verified over D-Bus rather than visually: `GetLayout` returns the full
recursive menu with live peer data, and a synthetic `Event` click on "Pause
Syncing" flipped `SyncPause` and rebuilt the menu to "Resume Syncing" with
`toggle-state 1`. **The icon itself has never been seen.** To check it:
`gnome-extensions enable ubuntu-appindicators@ubuntu.com`.

**Packaging.** `build-deb.sh` produces `dist/clipsync_0.8.0_amd64.deb` (~30
MB, self-contained runtime) with a `systemd --user` unit, an autostart
entry and `/usr/bin/clipsync`. Watch for two things that bite: `mktemp -d`
gives 0700 and `dpkg-deb` bakes that into the package root, and the daemon
must not exit when stdin is absent — under systemd `Console.ReadLine`
returns EOF immediately, which would look exactly like a clean shutdown.

### Still to do on Linux

- Visual confirmation of the tray icon (needs the extension enabled).
- Live XWayland restart: the reconnect loop is verified against a dead
  display but not against a real mid-session restart.
- Mac↔Linux clipboard sync has only been exercised in the sending
  direction; receiving needs someone to copy on the Mac.
- The 0.8.0 quality-of-life features — hidden devices, "Start over",
  start-at-login — are not implemented here yet.
- No settings UI: exclusions and pauses are console commands for now.

### What GNOME's tray actually supports — 2026-08-23

The tray was verified visually once `ubuntu-appindicators` was enabled (it
ships installed but INACTIVE on Ubuntu 26.04, and no extensions are enabled
by default, so `org.kde.StatusNotifierWatcher` is absent until someone turns
it on). Getting from "icon appears, clicking does nothing" to a working menu
turned up three separate defects, each of which fails silently:

1. **The introspection XML must be complete.** A stub declaring
   `<interface name="com.canonical.dbusmenu"/>` with no methods is enough
   for the icon to appear and for every property read to succeed, but the
   AppIndicator extension builds its menu proxy from introspection, so it
   ends up with nothing to call and never requests the menu at all. Nothing
   is logged on either side.
2. **The dbusmenu root item must have id 0.** Hosts call
   `GetLayout(0, ...)`; returning a root that identifies as any other id
   makes the host discard the whole layout. Same symptom as above — icon,
   no menu.
3. **`AboutToShow` must not announce a new revision unconditionally.**
   Rebuilding and emitting `LayoutUpdated` from inside `AboutToShow` makes
   the host re-read, which calls `AboutToShow` again: eleven `GetLayout`
   round trips in forty milliseconds for a menu nobody touched. Rebuild
   now compares a signature and only signals on a real change.

Two richer menu shapes were then tried and **both are impossible through
this host**, which is worth knowing before anyone attempts them again:

- **Nested submenus do not render.** The extension fetches the whole tree
  (verified: `GetLayout` returns the grandchildren correctly), draws the
  submenu arrow on the parent row, and never populates the child menu.
  Clicking the row does nothing.
- **Disclosure rows are not possible either.** The extension renders every
  dbusmenu entry as a `PopupMenuItem`, and activating one closes the whole
  popup — checkmark items included. A row that expands on click therefore
  costs a reopen before the disclosed action can be picked. There is no
  property in the dbusmenu protocol for "do not close on activate".

The Quick Settings panel can do disclosure because that is the Shell
drawing its own widgets. Anything arriving over dbusmenu is a flat list of
one-shot actions, and that is the ceiling for an SNI app on GNOME. The menu
is therefore flat with every action visible: device row, then its actions
indented beneath. **Anything needing a sequence of interactions belongs in
a settings window, not in the tray menu.**

Also fixed here: an Avahi `CollisionError` was fatal, so a second instance
(or a stale registration) killed the daemon at startup. The service name is
cosmetic — the did in the TXT record is the identity — so publishing now
retries as "name #2". This would have hit two machines sharing a hostname,
and under systemd it would have been a restart loop.

## Handoff 2026-08-23 — Linux app window replaces the tray menu

Design doc: `docs/superpowers/specs/2026-08-23-linux-app-window-design.md`.
Linux only; no protocol, settings-schema, or mac/Windows changes.

**What changed.** The flat tray menu stopped being the primary Linux UI.
Left-clicking the icon now opens a GTK4 + libadwaita window (Gir.Core
0.8.1 bindings, `GirCore.Adw-1` in the csproj) with the device list —
Trust with the 8-hex fingerprint for pending peers, a send switch for
trusted ones, hide/show — plus the global Pause Syncing switch and a
"This device" row. The dbusmenu shrank to a right-click fallback: Open
ClipSync / Pause Syncing / Quit ClipSync.

Mechanics: `ItemIsMenu` is now false and `Activate` /
`SecondaryActivate` open the window (`Ui/TrayIcon.cs`) — though see the
click-routing finding below for what GNOME actually sends. GTK runs on one
lazily-started background thread (`Ui/UiThread.cs`) — the daemon still
starts and syncs with no display, logging "the window is unavailable"
once. The window renders a pure, unit-tested model (`Ui/WindowModel.cs`,
`WindowModelTests`); `Ui/MainWindow.cs` adds no wording of its own. The
window hides on close; every UI action refreshes both the window and the
tray menu. Peer names are rendered with `use-markup` off — they arrive
off the network and Adw row titles are Pango markup by default.

**Icon parity (added later the same day).** The tray icon now matches the
Mac composition — clipboard glyph with a blue wifi badge, orange pause
bars when paused — instead of the stock `edit-copy-symbolic`. Two SVGs
(`icons/tray-*.svg`, drawn to mirror `StatusIcon.swift`, macOS system
blue and the Mac's dark-bar orange) are rendered at 22/44 px through
gdk-pixbuf's librsvg loader and served over the SNI `IconPixmap`
property (`Ui/TrayPixmaps.cs`); `IconName` goes empty so the host falls
through to the pixmap, and reverts to the stock names if rendering fails
(no librsvg, missing files). A pause flip now emits `NewIcon` +
`NewTitle` so hosts drop their cached copies. Two traps encountered:
GirCore's `Module.Initialize()` must run before any standalone
gdk-pixbuf call (the window path gets it via Adw implicitly); gdk-pixbuf
only recognizes an SVG whose *first bytes* are `<svg` — an XML prolog or
leading comment makes the file "unrecognizable"; and GirCore's
module/type registration is NOT thread-safe — pixmap init on the D-Bus
thread racing `Adw.Application.New` on the GTK thread threw a
`TypeInitializationException` and killed the window (found live: "open
no longer works"). `TrayPixmaps.EnsureLoaded()` now runs in Main before
discovery starts, so nothing can race it.

New debug affordances: `open` console command and `--open-window` flag.
The .deb now depends on `libgtk-4-1` and `libadwaita-1-0` (runtime only;
the bindings are managed P/Invoke, so building needs no native packages).

**Verified.** `dotnet build` clean; 56 tests pass (TrayMenuTests
rewritten for the three-item menu, WindowModelTests new). Live on the
real GNOME session, same day:

- Left and right click both show the three-item menu; "Open ClipSync"
  opens the window, which appears and renders correctly.
- Trust clicked in the window against Perry's Mac Studio; the Mac then
  trusted back; Hello completed, the row went Online (v0.7.1), and a
  clipboard item was broadcast to it. Window → trust → sync end-to-end.
- Text copies in BOTH directions with the Mac Studio — the first live
  proof of Mac→Linux receiving (a 217-byte text item arrived and landed
  in the clipboard, with no echo back).

**Click-routing finding (from the extension's source,
`indicatorStatusIcon.js`):** a single left click NEVER reaches
`Activate` while a menu is attached — the extension introspects for an
`Activate` method (`ItemIsMenu` is not consulted), and when found it
waits out the double-click timeout and then toggles the menu anyway.
Double-click calls `Activate`; middle click calls `SecondaryActivate`
(both open our window). Detaching the menu would NOT help: with no menu
items a single click does nothing at all. So menu-with-Open-first is the
ceiling for a single left click on GNOME; keep the menu.

**Still unverified:** double-click / middle-click opening the window
(wired, not yet tried), the send switch and hide button in the window.

**Stage 2, built later the same day:** the window gained the settings
groups. *Excluded apps*: rows with Remove plus a WM_CLASS entry field
(no picker by design — `xprop WM_CLASS` is the discovery tool; Gir.Core
0.8.1 does not bind Adw.EntryRow, so it is a plain Gtk.Entry, and its
in-progress text survives the rebuilds that peer churn triggers).
*"Start ClipSync when you sign in"*: `Platform/Autostart.cs` — the .deb
installs a system-wide /etc/xdg/autostart entry, so OFF writes a
`Hidden=true` override in ~/.config/autostart and ON removes it; with no
system entry (dev runs) ON writes a real user entry pointing at the
current binary. Unit-tested against temp dirs. *"Start over"*:
Adw.AlertDialog with the other platforms' wording ("computer" for
"PC"/"Mac"), then TrustStore.Clear + Settings.ResetAll + restart — under
systemd by exiting non-zero (Restart=on-failure relaunches, detected via
INVOCATION_ID), elsewhere by relaunching ProcessPath. 67 tests pass.
Live verification of stage 2 pending: window presents with the new
groups, but nothing has been clicked through yet.

Two findings from first use of the settings groups: a rebuild snapped
the scroll position back to the top (now carried across, restored at
below-redraw idle priority — a value set before layout is clamped to 0),
and **Linux now offers Hide on every device row, trusted included** —
the mac and Windows UIs offer it only on pending peers ("the other verb
an untrusted machine needs"), but a trusted machine can be worth
delisting too and hiding stays display-only, so it keeps syncing. This
is a deliberate parity deviation; consider mirroring it back to the
other two.

## Handoff 2026-09-08 — `clipsync` backgrounds itself from a shell

Linux only; no protocol, settings-schema, or mac/Windows changes. There is
nothing to mirror: the Windows client is a WinUI tray app and the Mac one
an app bundle, so neither has a shell invocation that could block a prompt.

**What changed.** Typing `clipsync` at a prompt now starts the daemon in
the background and returns, printing the pid and the log path.
`--foreground` (`-f`) keeps the old behaviour, console harness and all.
A second `clipsync` no longer starts a rival daemon; it opens the running
one's window and exits 0.

**The rule for which mode.** Background only when stdin is a terminal.
systemd's `Type=simple` unit and the XDG autostart entry both run without
one, so both keep the daemon in the process they started, and neither file
needed changing. `--self-test` and `--identity-only` print and exit, so
they are never backgrounded. `LaunchPlan` holds this as a pure function
with unit tests; everything below needed a live session and was checked by
hand.

**setsid(1), not setsid(2).** The first cut called `setsid()` from inside
the re-exec'd child and the daemon died a few hundred milliseconds after
the prompt came back — reliably, but only when launched from a real
terminal, which is why it survived the first round of testing. The CLR
takes long enough to start that the terminal is usually gone, and its
SIGHUP delivered, before `Main` runs. The child is now spawned through
`setsid(1)`, which makes the new session *before* exec and closes the
window; it execs in place rather than forking when the caller is not a
process group leader, so the pid printed to the user is the daemon's own.
`util-linux` is now declared in the .deb's `Depends` for it (it is
`Essential: yes`, so this is documentation rather than a new requirement).
The in-process `setsid()` remains as the fallback for a system without the
binary, where the race is still theoretically possible.

**The guard runs in the parent.** It first ran in the backgrounded child,
which was wrong in a way worth recording: the child had already detached
and truncated the running daemon's log by the time it discovered the
clash, so it printed "already running" into the log file rather than onto
the terminal waiting for it, and the user saw nothing. The parent now
probes before spawning.

Ownership of `org.clipsync.ClipSync` on the session bus is the guard —
atomic, and released when the owner dies, so there is no stale-pidfile
case. With no session bus the guard is skipped and the daemon runs, the
same posture as a missing tray host. `Adw.Application` stays `NonUnique`;
the guard sits above GTK, so the reasoning at `UiThread.cs:66` is
unchanged.

**Verified live** on Ubuntu under GNOME/XWayland: backgrounding from a pty
(daemon survives teardown, `SESS == PGID == PID`, no controlling
terminal), the log redirect, bus-name ownership, the second-launch guard
(message on the terminal, log not truncated, exactly one daemon), and the
no-terminal path staying in foreground. **Not verified:** that the window
visually appears on the second launch — the D-Bus call returns success,
which proves the handler ran and invoked `OnOpen`, but nothing confirms
what was drawn.

**Note for whoever packages next.** `dist/clipsync_0.8.0_amd64.deb` and
any installed copy predate all of this.
