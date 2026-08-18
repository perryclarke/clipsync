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

### To do on the Mac

1. `swift build` — expect possible small Swift fixes; the logic mirrors the
   C# byte-for-byte, so if something disagrees, the C# in
   `clipsync-win/ClipSync.Core/Net/` is the reference.
2. `swift test` — planner + assembler must pass; then
   `CLIPSYNC_TLS_LOOPBACK=1 swift test --filter StreamLoopback` for the
   5 MB item through two real `PeerConnection`s over mTLS.
3. Rebuild the app (`build-dmg.sh`) and run it against the Windows build on
   this branch. Windows is already running the new code, so until the Mac
   is rebuilt Windows logs `dropping <mime> (x MB): peer lacks stream
   capability` for >64 KiB formats — that line disappearing is the first
   sign both sides speak `stream`.
4. End-to-end: Retina screenshot Mac → Windows (TIFF + PNG both stream;
   Windows shows the PNG); >64 KiB text Windows → Mac; something over
   100 MB to see the drop line. Windows log: `--debug`,
   `%LOCALAPPDATA%\ClipSync\debug.log`; Mac: `log stream --predicate
   'process == "ClipSync"'`.

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
