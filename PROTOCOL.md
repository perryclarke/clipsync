# ClipSync Wire Protocol v1

Source of truth for every ClipSync implementation. Both the macOS and
Windows apps, and any future Linux port, MUST conform to this document.

## 1. Goals

1. Mirror clipboard activity between N peers on the same IPv6 subnet.
2. Zero cloud; discovery via mDNS only.
3. Confidentiality and authenticity against any device that has not been
   explicitly enrolled (pinned identity, mTLS 1.3).
4. One-time PIN enrollment; persistent trust thereafter.
5. Transparent to text, images, files, and arbitrary clipboard MIME data.

## 2. Identity

Each installation generates a long-lived **Ed25519** signing key on first
launch. The key is stored in the OS secure store (Keychain / DPAPI).

The **device_id** is the lowercase hex of the SHA-256 of the SPKI of the
device's self-signed certificate (see §4). The SPKI hash is also what
peers pin.

A device also has a human **name** (default: hostname) that is shown in
UIs. It is advertised but never trusted for authorisation.

## 3. Discovery (mDNS / DNS-SD)

Service type: `_clipsync._tcp.local.`
Port: any ephemeral TCP port the device binds to on IPv6.
Address records: AAAA only. IPv4 is not supported.

TXT record keys (UTF-8, `key=value`):

| Key      | Required | Meaning                                       |
|----------|----------|-----------------------------------------------|
| `v`      | yes      | Protocol version. `1` for this spec.          |
| `did`    | yes      | Device id (SPKI SHA-256 hex, 64 chars).       |
| `name`   | yes      | Display name, percent-encoded UTF-8.          |
| `caps`   | yes      | Comma list: `text,image,files,rich`.          |
| `pend`   | no       | `1` if device is not yet enrolled anywhere.   |

Browsers MUST ignore records whose `v` is not understood.

## 4. Transport

TCP over IPv6, wrapped in **TLS 1.3** with **mutual authentication**.
Both endpoints present a self-signed X.509 certificate whose SubjectPublicKeyInfo
carries the device's Ed25519 public key. The CN and SAN are `clipsync-<did>.local`.

Verification:

1. The normal PKI chain is ignored — there is no CA.
2. The peer's **SPKI SHA-256** MUST be in the receiver's trusted-peers
   store. If the hash is not present, and the peer is not currently in
   the enrollment-pending state for this side, the connection is closed
   immediately with TLS `unknown_ca` and no frames are sent.

Cipher suites: whatever the TLS library's default TLS 1.3 set is
(`TLS_AES_128_GCM_SHA256`, `TLS_AES_256_GCM_SHA384`,
`TLS_CHACHA20_POLY1305_SHA256`). No downgrade to TLS 1.2.

TCP keepalive is enabled, 30 s idle.

## 5. Framing

Every message is a length-prefixed CBOR map:

```
+-------------------+------------------------+
|  uint32 big-endian|  CBOR bytes (<= 16 MiB)|
|  length (N)       |                        |
+-------------------+------------------------+
```

Maximum single-frame size: 16 MiB. Anything larger MUST be streamed via
`FileChunk` messages (§6.5–§6.6).

Each frame's CBOR is a map with a required field `t` (uint8) that selects
the message type. All maps are **deterministic CBOR** (RFC 8949 §4.2.1
Core Deterministic Encoding).

## 6. Messages

Common field convention: every ClipboardItem/FileChunk carries `seq`, a
monotonically-increasing per-sender uint64. Receivers dedupe by
`(origin_did, seq)`.

### 6.1 Hello  (`t = 1`)

Exchanged immediately after TLS handshake, before anything else. Closes
the connection with `ProtocolError` on mismatch.

```
{
  t: 1,
  v: 1,                // protocol version
  did: bstr (32),      // raw SHA-256, not hex
  name: tstr,
  caps: [tstr, ...],
  ver: tstr            // app version, e.g. "0.7.1" (optional, ≥ 0.7.1)
}
```

`ver` is the sender's app version, informational only: it is shown in
the peer UI so a version mismatch between machines is visible from
either end. Feature gating stays on `caps`, never on `ver`. Receivers
MUST accept a Hello without it (any pre-0.7.1 peer).

Capabilities are free-form strings. Defined so far: `text`, `image`,
`files`, `rich`, and `stream` — the peer reassembles `FileChunk` /
`FileEnd` (§6.5–6.6). A sender MUST NOT stream to a peer that did not
advertise `stream`; it drops formats over the inline limit instead
(§10). Peers before 0.7 do not advertise it.

### 6.2 ClipboardItem  (`t = 2`)

Broadcast by the origin to every currently-connected trusted peer when
its local clipboard changes.

```
{
  t: 2,
  seq: uint,
  origin_did: bstr(32),
  ts_ms: uint,             // unix ms at origin
  formats: [
    {
      mime: tstr,          // e.g. "text/plain;charset=utf-8"
      size: uint,          // bytes
      inline: bstr?,       // present iff size <= 65536
      stream_id: uint?     // present iff size > 65536
    }, ...
  ],
  hint: tstr?              // optional free-text preview for UI
}
```

At most one of `inline` / `stream_id` per format. A format with
`stream_id` tells the receiver to expect matching `FileChunk` frames
before the item is considered complete. The receiver materializes every
streamed format back to inline bytes before acting on the item, so
everything downstream (clipboard writers, loop-prevention hash §8) sees
exactly the item the sender's watcher built.

### 6.3 LargeItemOffer  (`t = 3`)

**Reserved — not implemented.** Items over the cap are trimmed by the
sender (§10) rather than offered. Type numbers 3 and 4 are kept so they
are never reused.

Original definition: sent instead of inlining a ClipboardItem when the
total item size is > 100 MiB; receiver must opt in via `LargeItemAccept`.

```
{
  t: 3,
  seq: uint,
  origin_did: bstr(32),
  total_size: uint,
  formats: [ { mime: tstr, size: uint }, ... ],
  hint: tstr?
}
```

### 6.4 LargeItemAccept  (`t = 4`)

**Reserved — not implemented** (see §6.3).

```
{ t: 4, seq: uint, accept: bool }
```

If `accept = false` the sender discards the item silently.

### 6.5 FileChunk  (`t = 5`)

Carries a slice of a stream.

```
{
  t: 5,
  stream_id: uint,
  offset: uint,
  data: bstr    // <= 1 MiB
}
```

Ordering: after the `ClipboardItem` that references them, a sender emits
each stream's chunks contiguously and in order, followed by that stream's
`FileEnd`, then the next stream. Chunks of different streams are NOT
interleaved. `offset` MUST equal the number of bytes of that stream the
receiver has already accepted; a receiver drops the whole pending item on
any gap, repeat, or overrun of the declared `size`. `stream_id` values are
per connection and never reused within it.

A receiver holds at most one pending (streamed, incomplete) item per
connection. A new `ClipboardItem` with `stream_id` formats replaces an
incomplete one — the sender is sequential, so the older item is stale. A
pending item with no chunk progress for 30 s is dropped. Chunks for an
unknown `stream_id` are ignored without closing the connection.

### 6.6 FileEnd  (`t = 6`)

```
{
  t: 6,
  stream_id: uint,
  total_size: uint,
  sha256: bstr(32)
}
```

Receiver verifies SHA-256 before materializing the format. Mismatch
aborts the whole ClipboardItem.

### 6.7 Ack  (`t = 7`)

```
{ t: 7, seq: uint }
```

Optional — used by the UI to show "delivered" state. Protocol does not
require it.

### 6.8 Ping / Pong  (`t = 8`, `t = 9`)

Keepalive ping:  `{ t: 8, nonce: uint }`
Pong response:   `{ t: 9, nonce: uint }`

Sent every 20 s idle. Two missed pongs closes the connection.

### 6.9 ProtocolError  (`t = 10`)

```
{ t: 10, code: uint, msg: tstr? }
```

Sent immediately before the sender closes the connection. Codes:

| Code | Meaning                          |
|------|----------------------------------|
| 1    | Version mismatch                 |
| 2    | Untrusted peer                   |
| 3    | Bad framing / CBOR error         |
| 4    | Oversize frame                   |
| 5    | Hash mismatch                    |
| 6    | Timeout                          |
| 7    | Internal error                   |

## 7. Enrollment (SPAKE2 PIN pairing)

Performed once per device pair. Runs on a **separate** TCP connection on
the same port with TLS **disabled** — the exchange is self-protecting via
PAKE and exchanges long-term identities over an authenticated channel.

Preconditions: the joining device advertises itself with `pend=1` in its
TXT record. An already-trusted device's UI lists such devices and lets
the user click *Pair*.

Steps:

1. Trusted device generates and displays a 6-digit PIN (`000000`–`999999`,
   uniformly random). It opens a raw TCP connection to the pending device
   on its advertised port and sends:
   ```
   framed CBOR: { t: 128, salt: bstr(16) }
   ```
   The pending device rejects any non-enrollment frame until enrollment
   has either succeeded or the socket closes.

2. Both sides run **SPAKE2** (RFC 9383) with:
   - password = PIN string bytes,
   - identityA = trusted device's `did`, identityB = pending device's `did`,
   - `salt` as a context-binding AAD.
   Curve: **edwards25519**. Hash: SHA-256.

3. Derive a 32-byte session key `K`. Both sides derive confirm tags
   `cA = HMAC(K, "clipsync-confirm-A")` and `cB = HMAC(K, "clipsync-confirm-B")`
   and exchange them in messages `t = 129` and `t = 130`. On mismatch both
   sides abort and the PIN is burned.

4. Each side then sends:
   ```
   { t: 131, cert: bstr, did: bstr(32), name: tstr }
   ```
   encrypted with `AES-256-GCM` keyed by `K` (nonce = 12 zero bytes; safe
   because `K` is used for exactly one message in each direction).
   `cert` is the peer's self-signed X.509 DER.

5. Both sides persist each other's `did`, pinned SPKI hash, and display
   name into their trust stores, then close the enrollment connection.

6. From now on, normal §4 mTLS connections between the two devices
   succeed and clipboard sync begins.

Failure of any step abandons the PIN; the user must start over.

## 8. Loop prevention

When a device writes a remote `ClipboardItem` to its own clipboard, it
records `H = SHA-256(canonical_item_bytes)` in a short-lived set (TTL
5 s). The local watcher fires from the OS, computes the same hash over
the new clipboard content, and if the hash is in the set it does NOT
rebroadcast.

Additionally, every outgoing `ClipboardItem` carries `origin_did` set to
the **original** origin, not the forwarder. Peers that receive an item
whose `origin_did` equals their own `did` MUST drop it.

## 9. Multi-peer fanout

Each device maintains one open mTLS connection per trusted online peer.
On a local copy event it writes the same `ClipboardItem` frame to each
connection. There is no relaying: if A ↔ B and A ↔ C but B cannot see C,
items copied on A reach both B and C, but items copied on B never reach
C. This is intentional — minimal complexity and matches the
"everything on the subnet" mental model.

## 10. Size policy

- A format ≤ **64 KiB** is carried `inline`; larger formats are streamed
  (`stream_id` + `FileChunk`/`FileEnd`), and only to peers advertising
  the `stream` capability (§6.1). To other peers, formats over 64 KiB are
  dropped from the item.
- Item cap **100 MiB**, enforced by the sender: it walks the formats in
  order, keeping each while the running total stays within the cap and
  dropping the rest. If nothing fits, the item is not sent. Every drop is
  logged locally; nothing is sent to the peer about it.
- A receiver rejects (drops, without closing the connection) any
  `ClipboardItem` whose declared sizes exceed the cap, so a peer cannot
  make it allocate more than the sender would ever emit.
- Single `FileChunk` `data` MUST be ≤ 1 MiB.
- There is no offer/accept prompt (§6.3–6.4 reserved). Everything is held
  in memory end to end and the receiver briefly holds about twice the
  item size, which is why the cap should not be raised casually.

## 11. Versioning

This document is protocol **version 1**. Incompatible changes bump the
integer. Backwards-compatible additions (new optional map keys, new
message types with `t ≥ 200`) do not. Receivers MUST ignore unknown map
keys and SHOULD log-and-drop unknown message types without closing the
connection.
