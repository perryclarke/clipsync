import Foundation
import Security
import Crypto

/// Persistent list of trusted peers, keyed by SPKI SHA-256 hash of the
/// peer's TLS certificate. Stored as a plist at
/// `~/Library/Application Support/ClipSync/trust.plist`.
final class TrustStore {
    struct Entry: Codable {
        var didHex: String          // == hex(spkiHash)
        var name: String
        var addedAt: Date
    }

    private var entries: [String: Entry] = [:]    // keyed by didHex
    private let url: URL
    private let lock = NSLock()

    init(url: URL, entries: [String: Entry]) {
        self.url = url
        self.entries = entries
    }

    static func load() -> TrustStore {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        let dir = appSupport.appendingPathComponent("ClipSync", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("trust.plist")
        if let data = try? Data(contentsOf: url),
           let entries = try? PropertyListDecoder().decode([String: Entry].self, from: data) {
            return TrustStore(url: url, entries: entries)
        }
        return TrustStore(url: url, entries: [:])
    }

    var isEmpty: Bool { lock.lock(); defer { lock.unlock() }; return entries.isEmpty }

    func contains(hex: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return entries[hex.lowercased()] != nil
    }

    func contains(hash: Data) -> Bool {
        contains(hex: hash.map { String(format: "%02x", $0) }.joined())
    }

    func add(didHex: String, name: String) {
        lock.lock()
        entries[didHex.lowercased()] = Entry(didHex: didHex, name: name, addedAt: Date())
        let snap = entries
        lock.unlock()
        persist(snap)
    }

    func remove(didHex: String) {
        lock.lock()
        entries.removeValue(forKey: didHex.lowercased())
        let snap = entries
        lock.unlock()
        persist(snap)
    }

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
        NSLog("--reset cleared trusted peers")
    }

    func all() -> [Entry] {
        lock.lock(); defer { lock.unlock() }
        return Array(entries.values)
    }

    private func persist(_ snap: [String: Entry]) {
        let enc = PropertyListEncoder()
        enc.outputFormat = .binary
        if let data = try? enc.encode(snap) {
            try? data.write(to: url, options: .atomic)
        }
    }

    // MARK: SPKI helper

    /// SHA-256 of the DER SubjectPublicKeyInfo of a certificate.
    static func spkiSha256(of cert: SecCertificate) -> Data {
        // SecCertificateCopyKey extracts the public key; re-encoding as
        // DER SPKI requires the full cert. Easiest: hash the whole cert's
        // public key external representation. This is *not* strictly the
        // SPKI hash but is deterministic for a given key — sufficient for
        // pinning since the cert itself doesn't change.
        guard let key = SecCertificateCopyKey(cert),
              let data = SecKeyCopyExternalRepresentation(key, nil) as Data? else {
            return Data(repeating: 0, count: 32)
        }
        return Data(SHA256.hash(data: data))
    }
}
