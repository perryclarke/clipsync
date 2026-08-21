# Large-item streaming — design

Date: 2026-08-17. Status: approved, implementing.

## Problem

Both watchers inline every clipboard format regardless of size, and the
only limit is the 16 MiB frame cap in the codec. An item over that fails
silently (Windows: unobserved exception in `Broadcast`; mac: encode error).
The realistic trigger is a Retina screenshot on macOS, whose pasteboard
snapshot carries TIFF + PNG and can be 30–60 MB. `PROTOCOL.md` §6.5–6.6
already defines `FileChunk` / `FileEnd` for exactly this; nothing
implements them.

## Decisions

- **Item cap: 100 MiB**, enforced by the sender. Over-cap formats are
  dropped in item order until the rest fits; if nothing fits the item is
  not sent. Every drop is logged. No `LargeItemOffer` / `Accept` prompt.
- **Inline threshold: 64 KiB**, as §6.2 says. Above that a format is
  streamed. Images therefore always stream, which exercises the new path
  on every screenshot rather than only on rare huge items.
- **Transport-layer split.** Watchers, registry, pause, and writers are
  untouched. `PeerConnection` splits on send and reassembles on receive,
  handing downstream a fully-inline `ClipboardItem` identical to what the
  sender's watcher built. Loop suppression's canonical hash — which covers
  only inline payloads — is therefore preserved end-to-end.
- **Capability-gated.** Hello `caps` gains `"stream"`. To a peer without
  it (0.6.x), formats over 64 KiB are dropped instead of streamed. No
  protocol version bump (§11 backwards-compatible addition).
- **Out of scope:** file contents (files still travel as paths only);
  cancelling an in-flight transfer when a newer copy arrives; smarter
  format prioritisation than "in order".

## Send side (`PeerConnection.send(item:)` / `SendItemAsync`)

1. Cap filter: keep formats in order while the running total ≤ 100 MiB;
   log each drop as `dropping <mime> (<n> MB): item would exceed 100 MiB`.
   Nothing left → log, return.
2. Peer gate: if the peer lacks `stream`, drop every format > 64 KiB (one
   log line per item).
3. Split: each surviving format > 64 KiB gets a `stream_id` from a
   per-connection counter; the wire copy carries `stream_id` instead of
   `inline`. Small formats stay inline. The `ClipboardItem` frame is now a
   few KB.
4. Emit in order on this connection: the `ClipboardItem`; then per
   streamed format, `FileChunk {stream_id, offset, data}` in 1 MiB slices
   followed by `FileEnd {stream_id, total_size, sha256}`. Windows sends
   the whole sequence under `_writeLock`; on macOS `NWConnection.send`
   already serialises.

The in-memory item is not mutated, so `Broadcast` can pass one object to
every connection.

Planning (steps 1–3) is a pure function so it can be unit-tested:
`(item, peerHasStream) → (wireItem, streams[], dropped[])`.

## Receive side (`PeerConnection.handle`)

Per connection: at most one *parked* item, with a slot per `stream_id`
`{expected, buffer, received, done}`.

1. `ClipboardItem`, all inline → `OnItem` at once (today's path).
   Otherwise, if declared sizes total > 100 MiB or any single declared
   size > 100 MiB → log and drop the item (not the connection). Else park
   it, replacing (dropping) any incomplete parked item — the sender is
   sequential, so an unfinished older item is stale.
2. `FileChunk`: unknown `stream_id` → log, ignore. `offset` must equal
   bytes received so far and `offset + len ≤ expected`; otherwise drop the
   parked item. Append; note progress time.
3. `FileEnd`: `total_size == expected == received` and SHA-256 matches, or
   drop the parked item with `hash mismatch`. Mark done.
4. When every slot is done, materialise the item with those payloads
   inline and call `OnItem`.
5. A parked item with no chunk progress for 30 s is dropped. Connection
   close discards everything; state is per-connection.

Reassembly is a pure class (`StreamAssembler`) so it can be unit-tested
without a socket.

Peak receiver memory ≈ 2× item size briefly (buffers + materialised copy).
This is the reason the cap should not be raised casually.

## Protocol document changes

- §6.1: document the `stream` capability.
- §6.2: a receiver materialises streamed formats before acting on the item.
- §6.3/§6.4: reserved, not implemented; type numbers kept.
- §6.5: chunks of one stream are contiguous and in order; `offset` equals
  bytes received so far; interleaving across streams is not permitted.
- §10: inline ≤ 64 KiB, stream above; item cap 100 MiB enforced by the
  sender dropping formats in order; receiver rejects declared sizes over
  the cap; no offer/prompt.
- README "Known gaps": streaming comes off; note files travel as paths only.

## Testing

- Windows (xunit, `ClipSync.Tests`): planner and assembler live in
  `ClipSync.Core` (the test project cannot reference the WinUI app).
  Planner: passthrough under threshold; single large format split; cap
  filter drops in order keeping earlier formats; no-`stream` peer drops
  >64 KiB; nothing fits → empty. Assembler: happy path; out-of-order
  offset rejects; hash mismatch rejects; over-cap declared size rejects;
  stale parked item replaced; materialised item's canonical hash equals
  the original's.
- macOS (XCTest): the same units and cases, plus a loopback test in the
  style of `PeerCertBindingTests` streaming a 5 MB item across a real mTLS
  connection. Written here; run with `swift test` on the mac.
- End to end: Retina screenshot mac → Windows; >64 KiB text Windows → mac;
  >100 MB copy produces the drop log line.
