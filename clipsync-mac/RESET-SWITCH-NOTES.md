# `--reset` command-line switch — Mac verification notes

Written 2026-07-03 from the Windows side (this machine can't build Swift).
The Windows half is implemented **and tested**; the Mac half is written
conservatively and needs building + testing on the Mac. This documents exactly
what changed so it can be verified for behavioural parity with Windows.

## What `--reset` does

Forgets every trusted peer, so the user must re-approve (re-pair) connections.
It clears **only the trust store** — it does **not** touch the device identity
(Ed25519 key / TLS cert), so the device keeps its DID. After a reset the trust
store is empty, which means:

- `trustStore.isEmpty` is `true`, so Discovery advertises `pend=1` again (the
  device shows up as pairable to others).
- Incoming connections from previously-trusted peers are rejected as untrusted
  until they re-pair via the normal PIN enrollment flow.

This is a symmetric, per-device action: running `--reset` on one side only
resets that side. To fully re-pair, the user re-runs enrollment (which re-adds
trust on both ends).

## Mac changes (to verify)

### 1. `Sources/ClipSync/Security/TrustStore.swift`
Added two methods, mirroring the Windows `TrustStore.Clear()`:

```swift
/// Forget every trusted peer. After this the device advertises pend=1
/// again and rejects previously-trusted peers until they re-pair.
func clear() {
    lock.lock()
    entries.removeAll()
    let snap = entries
    lock.unlock()
    persist(snap)
}

/// Clear the persisted trust store on disk, before any instance is
/// loaded into memory. Used by the `--reset` command-line switch.
static func reset() {
    load().clear()
    NSLog("ClipSync: --reset cleared trusted peers")
}
```

`clear()` follows the exact snapshot-under-lock + `persist` pattern used by the
existing `add`/`remove`. `persist` writes the binary plist atomically to
`~/Library/Application Support/ClipSync/trust.plist`, so after reset that file
should decode to an empty `[String: Entry]`.

### 2. `Sources/ClipSync/ClipSyncApp.swift`
In `AppCoordinator.init()`, immediately **before** `TrustStore.load()`:

```swift
init() {
    self.identity = Identity.loadOrCreate()
    // `--reset` forgets all trusted peers so the user must re-approve
    // connections. Must run before the store is loaded into memory below.
    if CommandLine.arguments.contains("--reset") {
        TrustStore.reset()
    }
    self.trustStore = TrustStore.load()
    ...
}
```

**Why here and not in `ClipSyncApp.init()`:** `@StateObject var coordinator =
AppCoordinator()` is created lazily by SwiftUI (the `@autoclosure`), so the
reset must sit right before the load it needs to precede. Putting the clear
immediately before `TrustStore.load()` in the same `init` guarantees the store
is loaded empty regardless of SwiftUI's construction timing.

## How to verify on the Mac

1. Build and confirm the trust store currently has ≥1 peer (pair with a device,
   or inspect `~/Library/Application Support/ClipSync/trust.plist`):
   ```sh
   plutil -p ~/Library/Application\ Support/ClipSync/trust.plist
   ```
2. **Back up** the plist first (reset is destructive to pairings):
   ```sh
   cp ~/Library/Application\ Support/ClipSync/trust.plist /tmp/trust.plist.bak
   ```
3. Launch with the switch (args aren't delivered on a normal Finder launch —
   use the binary directly or `open --args`):
   ```sh
   /path/to/ClipSync.app/Contents/MacOS/ClipSync --reset
   # or:  open -a ClipSync --args --reset
   ```
4. Confirm:
   - `Console.app` / `log stream` shows `ClipSync: --reset cleared trusted peers`.
   - `plutil -p …/trust.plist` now prints an empty dict `{}`.
   - The menu bar lists no trusted peers, and a previously-paired device shows
     as pending / requires re-pairing.
5. Restore if desired: `cp /tmp/trust.plist.bak …/trust.plist` (with the app
   stopped) and relaunch.

## Windows parity reference (already tested)

- `Security/TrustStore.cs`: added `Clear()` (clears `_entries`, `Persist()`).
- `Program.cs`: parses `--reset` (and `/reset`) **before** `Application.Start`,
  calling `TrustStore.Load().Clear()`; composes with `--debug`.
- Verified: `trust.json.dpapi` went from 2 entries to `{}`; log emitted
  `Program: --reset clearing trusted peers`. (Real store was backed up/restored.)

Behavioural contract to match: **reset clears trusted peers only, before the
store loads, then the app continues running normally with an empty store.**
