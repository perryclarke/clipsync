# ClipSync

LAN-only, end-to-end encrypted clipboard sync between macOS 15+ and
Windows 11 25H2+. No cloud. mDNS + mTLS 1.3 over IPv6. Items land in
both the active clipboard and the OS clipboard history (macOS 15
Clipboard History / Win+V).

- **PROTOCOL.md** — wire format and state machines (source of truth for
  every implementation).
- **clipsync-mac/** — Swift 6 / SwiftUI `MenuBarExtra` app. Build with
  `swift build -c release` inside `clipsync-mac/`; to ship as a real
  `.app` wrap the binary with the provided `Info.plist` and
  Developer-ID-sign + notarize it.
- **clipsync-win/** — .NET 9 / WinUI 3 tray app. Open `ClipSync.sln` in
  Visual Studio 2022 17.12+ with the Windows App SDK 1.6 workload, or
  `dotnet build clipsync-win/ClipSync/ClipSync.csproj -c Release`.

## Known gaps in this scaffold

The full build order (see `/Users/perry/.claude/plans/magical-cooking-storm.md`)
is 11 steps. This scaffold lands steps 1–9. Three items are intentionally
stubbed with TODOs and need to be completed before the app will sync
end-to-end:

1. **macOS self-signed SecIdentity builder** — `Identity.swift` has a
   placeholder `createSelfSignedSecIdentity()`. Implement a tiny DER
   writer for a P-256 self-signed cert and hand it to
   `SecItemAdd(kSecClassIdentity)`. Once present, `TLS.swift` will start
   presenting a real local identity.
2. **SPAKE2 group math** — both `Enrollment.swift` and `Enrollment.cs`
   set up HKDF, confirm tags, and per-direction AES-256-GCM sub-keys,
   but the actual RFC 9383 SPAKE2 exchange over edwards25519 is not
   vendored. Port from the Matter/CHIP reference or Go `x/crypto` and
   feed its shared secret into `derive(from:)` / `Derive()`.
3. **Large-item offer/accept flow** — the `LargeItemOffer` /
   `LargeItemAccept` / `FileChunk` / `FileEnd` path is specified in
   `PROTOCOL.md` but only the inline-payload path is wired up in code.
   The 100 MB prompt UI and streamed file transfer are the last chunks
   of work.

Everything else — discovery, transport skeleton, CBOR framing, clipboard
watchers and writers with loop suppression, trust store persistence,
menu-bar / tray UI — is in place and internally consistent across both
codebases.
