import Foundation
import Crypto
import SwiftCBOR

/// Wire-level types that mirror PROTOCOL.md §6. Keep field names in sync
/// with the Windows implementation — CBOR keys are what matter.

enum ClipPayload {
    case inline(Data)
    case stream(UInt64)        // stream_id
}

struct ClipFormat {
    var mime: String
    var size: UInt64
    var payload: ClipPayload
}

struct ClipboardItem {
    var seq: UInt64
    var originDid: Data    // 32 bytes
    var tsMs: UInt64
    var formats: [ClipFormat]
    var hint: String?

    /// Canonical hash used for loop suppression. Hashes the sorted
    /// (mime,bytes) pairs; seq/ts/origin are excluded so a receiver that
    /// re-broadcasts an identical payload produces the same hash.
    var canonicalHash: Data {
        var hasher = SHA256()
        let sorted = formats.sorted { $0.mime < $1.mime }
        for f in sorted {
            hasher.update(data: Data(f.mime.utf8))
            hasher.update(data: [0])
            if case .inline(let d) = f.payload { hasher.update(data: d) }
            hasher.update(data: [0xff])
        }
        return Data(hasher.finalize())
    }
}

// MARK: - Encoding

enum MessageType: UInt8 {
    case hello = 1
    case clipboardItem = 2
    case largeItemOffer = 3
    case largeItemAccept = 4
    case fileChunk = 5
    case fileEnd = 6
    case ack = 7
    case ping = 8
    case pong = 9
    case protocolError = 10
    // enrollment
    case enrollSalt = 128
    case enrollConfirmA = 129
    case enrollConfirmB = 130
    case enrollIdentity = 131
}

enum CodecError: Error { case invalid, oversize, unknownType }

enum Codec {
    static let maxFrameSize = 16 * 1024 * 1024

    static func encode(_ m: [CBOR: CBOR]) -> Data {
        let bytes = CBOR.map(m).encode()
        var out = Data(count: 4)
        let len = UInt32(bytes.count).bigEndian
        withUnsafeBytes(of: len) { out.replaceSubrange(0..<4, with: $0) }
        out.append(contentsOf: bytes)
        return out
    }

    static func encodeHello(did: Data, name: String, caps: [String]) -> Data {
        encode([
            "t": .unsignedInt(UInt64(MessageType.hello.rawValue)),
            "v": .unsignedInt(1),
            "did": .byteString(Array(did)),
            "name": .utf8String(name),
            "caps": .array(caps.map { .utf8String($0) })
        ])
    }

    static func encodePing() -> Data {
        encode(["t": .unsignedInt(UInt64(MessageType.ping.rawValue))])
    }

    static func encodePong() -> Data {
        encode(["t": .unsignedInt(UInt64(MessageType.pong.rawValue))])
    }

    static func encodeClipboardItem(_ item: ClipboardItem) -> Data {
        var formats: [CBOR] = []
        for f in item.formats {
            var m: [CBOR: CBOR] = [
                "mime": .utf8String(f.mime),
                "size": .unsignedInt(f.size)
            ]
            switch f.payload {
            case .inline(let d): m["inline"] = .byteString(Array(d))
            case .stream(let id): m["stream_id"] = .unsignedInt(id)
            }
            formats.append(.map(m))
        }
        var top: [CBOR: CBOR] = [
            "t": .unsignedInt(UInt64(MessageType.clipboardItem.rawValue)),
            "seq": .unsignedInt(item.seq),
            "origin_did": .byteString(Array(item.originDid)),
            "ts_ms": .unsignedInt(item.tsMs),
            "formats": .array(formats)
        ]
        if let h = item.hint { top["hint"] = .utf8String(h) }
        return encode(top)
    }

    static func encodeFileChunk(streamId: UInt64, offset: UInt64, data: Data) -> Data {
        encode([
            "t": .unsignedInt(UInt64(MessageType.fileChunk.rawValue)),
            "stream_id": .unsignedInt(streamId),
            "offset": .unsignedInt(offset),
            "data": .byteString(Array(data))
        ])
    }

    static func encodeFileEnd(streamId: UInt64, totalSize: UInt64, sha256: Data) -> Data {
        encode([
            "t": .unsignedInt(UInt64(MessageType.fileEnd.rawValue)),
            "stream_id": .unsignedInt(streamId),
            "total_size": .unsignedInt(totalSize),
            "sha256": .byteString(Array(sha256))
        ])
    }

    static func decodeFileChunk(_ cbor: CBOR) -> (streamId: UInt64, offset: UInt64, data: Data)? {
        guard case let .map(m) = cbor,
              case let .unsignedInt(sid)? = m["stream_id"],
              case let .unsignedInt(off)? = m["offset"],
              case let .byteString(d)? = m["data"] else { return nil }
        return (sid, off, Data(d))
    }

    static func decodeFileEnd(_ cbor: CBOR) -> (streamId: UInt64, totalSize: UInt64, sha256: Data)? {
        guard case let .map(m) = cbor,
              case let .unsignedInt(sid)? = m["stream_id"],
              case let .unsignedInt(total)? = m["total_size"],
              case let .byteString(h)? = m["sha256"] else { return nil }
        return (sid, total, Data(h))
    }

    /// Capabilities from a Hello; empty if absent.
    static func decodeHelloCaps(_ cbor: CBOR) -> Set<String> {
        guard case let .map(m) = cbor, case let .array(arr)? = m["caps"] else { return [] }
        var caps = Set<String>()
        for c in arr { if case let .utf8String(s) = c { caps.insert(s) } }
        return caps
    }

    static func decodeFrame(_ payload: Data) throws -> CBOR {
        guard let decoded = try? CBOR.decode(Array(payload)) else { throw CodecError.invalid }
        return decoded
    }

    static func messageType(_ cbor: CBOR) -> MessageType? {
        guard case let .map(m) = cbor,
              case let .unsignedInt(raw)? = m["t"],
              let t = MessageType(rawValue: UInt8(truncatingIfNeeded: raw)) else { return nil }
        return t
    }

    static func decodeClipboardItem(_ cbor: CBOR) -> ClipboardItem? {
        guard case let .map(m) = cbor,
              case let .unsignedInt(seq)? = m["seq"],
              case let .byteString(did)? = m["origin_did"],
              case let .unsignedInt(ts)? = m["ts_ms"],
              case let .array(fms)? = m["formats"] else { return nil }
        var formats: [ClipFormat] = []
        for fcb in fms {
            guard case let .map(fm) = fcb,
                  case let .utf8String(mime)? = fm["mime"],
                  case let .unsignedInt(size)? = fm["size"] else { return nil }
            let payload: ClipPayload
            if case let .byteString(b)? = fm["inline"] {
                payload = .inline(Data(b))
            } else if case let .unsignedInt(sid)? = fm["stream_id"] {
                payload = .stream(sid)
            } else { return nil }
            formats.append(ClipFormat(mime: mime, size: size, payload: payload))
        }
        var hint: String? = nil
        if case let .utf8String(h)? = m["hint"] { hint = h }
        return ClipboardItem(seq: seq, originDid: Data(did), tsMs: ts, formats: formats, hint: hint)
    }
}
