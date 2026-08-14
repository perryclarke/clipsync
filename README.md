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
- **clipsync-win/** — .NET 10 / WinUI 3 tray app. Open `ClipSync.sln` in
  Visual Studio 2022 17.12+ with the Windows App SDK 1.6 workload, or
  `dotnet build clipsync-win/ClipSync/ClipSync.csproj -c Release`.

  Sending can be paused from the tray menu. **Pause syncing**, between
  **Settings…** and **Quit**, stops this device sending anything to
  anybody; the title reads "ClipSync — Paused" and the tray tooltip
  follows, so it is visible without opening the menu. Each known peer
  also has its own ⏸ / ▶ button, which pauses sending to just that
  machine. Both are send-only: items from peers still arrive and still
  land in your clipboard while paused, and nothing is queued or replayed
  on resume. A per-peer pause is remembered across restarts; a global one
  deliberately is not, so a reboot can never leave you silently not
  syncing.

  Apps can be excluded from sync: open the tray menu → **Settings…**, which
  opens beside the tray popup rather than over it, then **Add app** →
  **Choose an installed app…** and pick from the installed-app list, which
  covers Store / UWP apps as well as desktop ones. **Browse…** in that
  dialog picks an `.exe` directly, for anything the list misses. Anything
  copied while an
  excluded app is in the foreground stays local — it is still placed in
  your own clipboard and Win+V history, but never sent to a peer. If
  ClipSync cannot identify the foreground app — some system and protected
  processes cannot be inspected — the item is treated as not excluded and
  is synced.

  Desktop apps are matched on the executable's file name, which the list
  reads from the Start Menu shortcut's target. Some apps start through a
  stub — a launcher, or a Squirrel `Update.exe` — that is not the
  executable owning the window you copy from, so picking them from the list
  has no effect. If an exclusion does not take, use **Add app** →
  **Exclude the app I switch to…** instead: it counts down five seconds
  while you switch to the app,
  then records whatever is actually in the foreground, which is by
  construction what the matching sees. (**Browse…** to the real `.exe`
  works too.) Where several Start Menu entries share one executable — a
  dozen share `cmd.exe` — they appear as a single row labelled "… (and 11
  others)", because excluding one excludes all of them.

## Known gaps

Discovery, mTLS transport, CBOR framing, clipboard watchers and writers
with loop suppression, trust-store persistence, and the menu-bar / tray
UI are in place and internally consistent across both codebases. Two
features remain before the app matches the full spec:

1. **PIN pairing (SPAKE2) is not wired up.** Today, trust is established
   **TOFU**: pending peers appear in the menu and you click *Trust* to
   pin them (`MenuBarView.swift` → `TrustStore.add`). The PIN-gated
   enrollment in `PROTOCOL.md` §7 is unbuilt — `EnrollmentSession`
   derives the session key, confirm tags, and per-direction AES-256-GCM
   sub-keys, but the SPAKE2 exchange itself, the enrollment transport,
   and the pairing UI (`PairWindow.xaml.cs` is a TODO) do not exist yet.
   Note: §7 cites RFC 9383 (SPAKE2+), but the symmetric shared-PIN design
   is balanced SPAKE2 (**RFC 9382**) — implement 9382 and correct the
   citation.
2. **Large-item / streaming flow.** Inline payloads (≤ 64 KiB) sync
   today. The `LargeItemOffer` / `LargeItemAccept` / `FileChunk` /
   `FileEnd` path (`PROTOCOL.md` §6.3–6.6) has its message types and the
   `stream_id` payload variant defined, but no codecs or handlers, so
   anything larger than 64 KiB is not yet transferred. The >100 MiB
   prompt UI and streamed file transfer are the remaining work.

The macOS self-signed `SecIdentity` builder (`Identity.swift`
`createKeychainIdentity()`) and its Windows counterpart
(`Identity.cs` `CreateSelfSignedCert()`) are **complete** — earlier
revisions of this list described them as stubs; they no longer are.
