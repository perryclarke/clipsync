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
