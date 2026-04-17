import Foundation
import Crypto

/// Enrollment is the PIN-gated, one-shot identity exchange described in
/// PROTOCOL.md §7. It runs on a *raw* TCP connection (no TLS) because
/// the pair doesn't yet trust each other's certs — SPAKE2 itself is
/// what makes the exchange safe.
///
/// This file stubs the SPAKE2 state machine interface and derives the
/// confirm tags / session key; the actual SPAKE2 group math lives in
/// `SPAKE2Ed25519.swift` (see TODO at bottom) and is omitted from this
/// scaffold — swift-crypto does not ship SPAKE2 yet, so a small vendored
/// RFC 9383 implementation is needed. The structure here is what the UI
/// calls into once that lands.

struct EnrollmentSession {
    enum Side { case initiator, responder }
    let side: Side
    let pin: String
    let ourDid: Data
    let theirDid: Data
    let salt: Data
    private(set) var sessionKey: Data?

    mutating func derive(from spake2Secret: Data) {
        // HKDF-SHA256(spake2Secret, salt, "clipsync-enroll-v1", 32)
        let prk = HMAC<SHA256>.authenticationCode(
            for: spake2Secret, using: SymmetricKey(data: salt))
        let key = HMAC<SHA256>.authenticationCode(
            for: Data("clipsync-enroll-v1".utf8), using: SymmetricKey(data: Data(prk)))
        self.sessionKey = Data(key)
    }

    func confirmTags() -> (a: Data, b: Data)? {
        guard let k = sessionKey else { return nil }
        let key = SymmetricKey(data: k)
        let a = HMAC<SHA256>.authenticationCode(for: Data("clipsync-confirm-A".utf8), using: key)
        let b = HMAC<SHA256>.authenticationCode(for: Data("clipsync-confirm-B".utf8), using: key)
        return (Data(a), Data(b))
    }

    /// AES-256-GCM seal/open with a zero nonce (safe: the key is used
    /// for at most one message per direction, and directions have
    /// distinct sub-keys derived from HKDF info below).
    func seal(_ plaintext: Data, direction: String) throws -> Data {
        guard let k = sessionKey else { throw CodecError.invalid }
        let subkey = HMAC<SHA256>.authenticationCode(
            for: Data(direction.utf8), using: SymmetricKey(data: k))
        let nonce = try AES.GCM.Nonce(data: Data(repeating: 0, count: 12))
        let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: Data(subkey)), nonce: nonce)
        return box.ciphertext + box.tag
    }

    func open(_ sealed: Data, direction: String) throws -> Data {
        guard let k = sessionKey, sealed.count >= 16 else { throw CodecError.invalid }
        let subkey = HMAC<SHA256>.authenticationCode(
            for: Data(direction.utf8), using: SymmetricKey(data: k))
        let nonce = try AES.GCM.Nonce(data: Data(repeating: 0, count: 12))
        let ct = sealed.prefix(sealed.count - 16)
        let tag = sealed.suffix(16)
        let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ct, tag: tag)
        return try AES.GCM.open(box, using: SymmetricKey(data: Data(subkey)))
    }
}

// TODO: vendor an RFC 9383 SPAKE2 over edwards25519 implementation. Feed
// its output secret into `EnrollmentSession.derive(from:)`. Swift-crypto
// does not currently ship SPAKE2; see e.g. the Matter/CHIP reference
// implementation or go-spake2 for a clean port.
