import Foundation
import Security
import Crypto
import X509
import SwiftASN1

/// Persistent device identity.
///
/// Key generation goes through `SecKeyCreateRandomKey` with
/// `kSecAttrIsPermanent = true`. On ad-hoc signed apps without a Team ID
/// entitlement this is the only Keychain path that works — the
/// data-protection-keychain `SecItemAdd(kSecValueRef: secKey)` route
/// errors with `errSecMissingEntitlement (-34018)`.
///
/// We build the TLS cert with swift-certificates for structure, then
/// extract the TBS bytes and re-sign them with the permanent SecKey so
/// the resulting certificate binds cleanly to the Keychain-resident
/// private key and `SecIdentityCreateWithCertificate` can find it.
final class Identity {
    /// Globally accessible reference to the current process's identity,
    /// populated by `loadOrCreate()`.
    static var shared: Identity!

    let did: Data                // 32 bytes
    let didHex: String
    let secIdentity: SecIdentity
    let secCertificate: SecCertificate
    let ed25519Private: Curve25519.Signing.PrivateKey

    private init(did: Data,
                 secIdentity: SecIdentity,
                 cert: SecCertificate,
                 ed: Curve25519.Signing.PrivateKey) {
        self.did = did
        self.didHex = did.map { String(format: "%02x", $0) }.joined()
        self.secIdentity = secIdentity
        self.secCertificate = cert
        self.ed25519Private = ed
    }

    private static let label = "ClipSync TLS Identity"
    /// Human-readable name shown in the macOS "ClipSync wants to sign using
    /// key …" keychain prompt. This is only the key's display label; the key
    /// is looked up by `keyTag` (application tag), never by this string, so it
    /// is safe to set independently of `label` (which keys cert/identity
    /// lookups and must not change for existing installs).
    private static let keyDisplayLabel = "ClipSync device identity"
    private static let keyTag = "com.clipsync.tls.key".data(using: .utf8)!
    private static let edService = "com.clipsync.identity"
    private static let edAccount = "clipsync-ed25519"
    private static let certService = "com.clipsync.identity"
    private static let certAccount = "clipsync-tls-cert-der"

    static func loadOrCreate() -> Identity {
        let identity = tryLoad() ?? create()
        ensureLabels()      // friendly keychain-prompt names; migrates old items
        pruneStaleCerts()   // remove duplicate certs from earlier (re)creation
        Identity.shared = identity
        return identity
    }

    /// Remove stale ClipSync certificates left in the keychain by earlier
    /// identity (re)creation. Each `create()` run added a fresh cert with a
    /// new random serial, so duplicates accumulate over time. Keep only the
    /// canonical cert — the one whose DER matches the stashed cert the current
    /// identity resolves against — and delete the rest. Certificates aren't
    /// secret, so deleting them doesn't trigger an auth prompt. No-op when the
    /// stashed DER is unavailable (then we can't tell which one to keep).
    private static func pruneStaleCerts() {
        guard let canonical = loadCertDER() else { return }
        let q: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecAttrLabel as String: "clipsync",   // CN-derived label of our certs
            kSecMatchLimit as String: kSecMatchLimitAll,
            kSecReturnRef as String: true,
            kSecReturnData as String: true,
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let items = out as? [[String: Any]] else { return }
        var removed = 0
        for item in items {
            guard let der = item[kSecValueData as String] as? Data,
                  der != canonical,                        // never delete the live cert
                  let ref = item[kSecValueRef as String] else { continue }
            let cert = ref as! SecCertificate
            // Extra safety: only prune certs whose subject CN is "clipsync",
            // so an unrelated cert that happens to share the label is untouched.
            var cn: CFString?
            SecCertificateCopyCommonName(cert, &cn)
            guard (cn as String?) == "clipsync" else { continue }
            let del: [String: Any] = [
                kSecClass as String: kSecClassCertificate,
                kSecValueRef as String: cert,
            ]
            if SecItemDelete(del as CFDictionary) == errSecSuccess { removed += 1 }
        }
        if removed > 0 { NSLog("pruned \(removed) stale certificate(s)") }
    }

    /// Give every keychain item backing the device identity a friendly,
    /// human-readable label so the macOS access/signing prompts name
    /// "ClipSync device identity" instead of `<key>` or the raw service string
    /// `com.clipsync.identity`. All of these are metadata-only reads/updates —
    /// they never touch the secret data, so they don't trigger an auth prompt.
    private static func ensureLabels() {
        ensureKeyLabel()
        ensureGenericPasswordLabel(account: edAccount)
        ensureGenericPasswordLabel(account: certAccount)
    }

    /// Set a friendly `kSecAttrLabel` on a generic-password item (the raw
    /// Ed25519 key or the stashed cert DER). Without a label macOS shows the
    /// service name (`com.clipsync.identity`) in the access prompt. Reads the
    /// current label first and only writes when it differs, so launches stay
    /// silent once migrated.
    private static func ensureGenericPasswordLabel(account: String) {
        let read: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: edService,
            kSecAttrAccount as String: account,
            kSecReturnAttributes as String: true,
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(read as CFDictionary, &out) == errSecSuccess,
              let attrs = out as? [String: Any] else { return }
        if (attrs[kSecAttrLabel as String] as? String) == keyDisplayLabel { return }

        let match: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: edService,
            kSecAttrAccount as String: account,
        ]
        let update: [String: Any] = [kSecAttrLabel as String: keyDisplayLabel]
        let s = SecItemUpdate(match as CFDictionary, update as CFDictionary)
        if s == errSecSuccess {
            NSLog("set label on \(account) to \"\(keyDisplayLabel)\"")
        } else if s != errSecItemNotFound {
            NSLog("label update for \(account) failed: \(s)")
        }
    }

    /// Ensure the permanent signing key carries a human-readable label, so the
    /// macOS "ClipSync wants to sign using key …" prompt shows a meaningful
    /// name instead of the placeholder `<key>`. Reads the current label first
    /// (a metadata read, no auth prompt) and only writes when it differs, so
    /// repeated launches stay silent. Also migrates keys from older builds.
    private static func ensureKeyLabel() {
        let read: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: keyTag,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecReturnAttributes as String: true,
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(read as CFDictionary, &out) == errSecSuccess,
              let attrs = out as? [String: Any] else { return }
        if (attrs[kSecAttrLabel as String] as? String) == keyDisplayLabel { return }

        let match: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: keyTag,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
        ]
        let update: [String: Any] = [kSecAttrLabel as String: keyDisplayLabel]
        let s = SecItemUpdate(match as CFDictionary, update as CFDictionary)
        if s == errSecSuccess {
            NSLog("set signing key label to \"\(keyDisplayLabel)\"")
        } else {
            NSLog("ensureKeyLabel update failed: \(s)")
        }
    }

    // MARK: - Load

    private static func tryLoad() -> Identity? {
        guard let ed = loadEd25519() else { return nil }
        guard let (secIdentity, secCert) = loadSecIdentity() else { return nil }
        let did = TrustStore.spkiSha256(of: secCert)
        return Identity(did: did, secIdentity: secIdentity, cert: secCert, ed: ed)
    }

    private static func loadEd25519() -> Curve25519.Signing.PrivateKey? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: edService,
            kSecAttrAccount as String: edAccount,
            kSecReturnData as String: true
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data,
              let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: data) else {
            return nil
        }
        return key
    }

    private static func loadSecIdentity() -> (SecIdentity, SecCertificate)? {
        // Path 0: load the cert DER from generic-password storage and
        // resolve an identity against the permanent key. This is the
        // path we rely on; it side-steps kSecClassCertificate/Identity
        // quirks on ad-hoc signed apps.
        if let der = loadCertDER(),
           let cert = SecCertificateCreateWithData(nil, der as CFData) {
            var id: SecIdentity?
            let s = SecIdentityCreateWithCertificate(nil, cert, &id)
            if s == errSecSuccess, let id { return (id, cert) }
            NSLog("SecIdentityCreateWithCertificate from stashed DER failed: \(s)")
        }

        // Path 1: direct identity lookup by label.
        let q1: [String: Any] = [
            kSecClass as String: kSecClassIdentity,
            kSecAttrLabel as String: label,
            kSecReturnRef as String: true
        ]
        var out: AnyObject?
        let s1 = SecItemCopyMatching(q1 as CFDictionary, &out)
        if s1 == errSecSuccess, let ref = out {
            let identity = ref as! SecIdentity
            var cert: SecCertificate?
            SecIdentityCopyCertificate(identity, &cert)
            if let c = cert { return (identity, c) }
        }
        NSLog("identity-by-label lookup failed: \(s1)")

        // Path 2: find our cert by label, then resolve an identity from it.
        // The permanent key is keyed by applicationTag — as long as both are
        // still in the keychain, SecIdentityCreateWithCertificate succeeds.
        let q2: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecAttrLabel as String: label,
            kSecReturnRef as String: true
        ]
        var certOut: AnyObject?
        let s2 = SecItemCopyMatching(q2 as CFDictionary, &certOut)
        if s2 == errSecSuccess, let ref = certOut {
            let cert = ref as! SecCertificate
            var id: SecIdentity?
            let s3 = SecIdentityCreateWithCertificate(nil, cert, &id)
            if s3 == errSecSuccess, let id { return (id, cert) }
            NSLog("SecIdentityCreateWithCertificate from stored cert failed: \(s3)")
        } else {
            NSLog("cert-by-label lookup failed: \(s2)")
        }
        return nil
    }

    // MARK: - Create

    private static func create() -> Identity {
        let ed = loadEd25519() ?? {
            let fresh = Curve25519.Signing.PrivateKey()
            storeEd25519(fresh)
            return fresh
        }()

        let (secIdentity, secCert) = createKeychainIdentity()
        let did = TrustStore.spkiSha256(of: secCert)
        return Identity(did: did, secIdentity: secIdentity, cert: secCert, ed: ed)
    }

    private static func storeCertDER(_ der: Data) {
        let add: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: certService,
            kSecAttrAccount as String: certAccount,
            kSecAttrLabel as String: keyDisplayLabel,
            kSecValueData as String: der,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
        ]
        SecItemDelete(add as CFDictionary)
        let s = SecItemAdd(add as CFDictionary, nil)
        if s != errSecSuccess {
            NSLog("storeCertDER failed: \(s)")
        }
    }

    private static func loadCertDER() -> Data? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: certService,
            kSecAttrAccount as String: certAccount,
            kSecReturnData as String: true
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data else { return nil }
        return data
    }

    private static func storeEd25519(_ key: Curve25519.Signing.PrivateKey) {
        let add: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: edService,
            kSecAttrAccount as String: edAccount,
            kSecAttrLabel as String: keyDisplayLabel,
            kSecValueData as String: key.rawRepresentation,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
        ]
        SecItemDelete(add as CFDictionary)
        SecItemAdd(add as CFDictionary, nil)
    }

    private static func lookupPermanentKey() -> SecKey? {
        let q: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: keyTag,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecReturnRef as String: true
        ]
        var out: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess else { return nil }
        return (out as! SecKey)
    }

    private static func createKeychainIdentity() -> (SecIdentity, SecCertificate) {
        // 1. Reuse an existing permanent key if one is still around —
        //    that keeps the did stable across launches even if the
        //    stashed cert DER got wiped. Only generate a new key when
        //    there genuinely isn't one.
        let secPrivKey: SecKey
        if let existing = lookupPermanentKey() {
            secPrivKey = existing
        } else {
            let keyParams: [String: Any] = [
                kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
                kSecAttrKeySizeInBits as String: 256,
                kSecPrivateKeyAttrs as String: [
                    kSecAttrIsPermanent as String: true,
                    kSecAttrApplicationTag as String: keyTag,
                    kSecAttrLabel as String: keyDisplayLabel,
                ] as [String: Any]
            ]
            var err: Unmanaged<CFError>?
            guard let k = SecKeyCreateRandomKey(keyParams as CFDictionary, &err) else {
                fatalError("ClipSync: SecKeyCreateRandomKey failed: \(err!.takeRetainedValue())")
            }
            secPrivKey = k
        }
        guard let secPubKey = SecKeyCopyPublicKey(secPrivKey),
              let pubData = SecKeyCopyExternalRepresentation(secPubKey, nil) as Data? else {
            fatalError("ClipSync: failed to extract public key")
        }
        let p256Pub: P256.Signing.PublicKey
        do {
            p256Pub = try P256.Signing.PublicKey(x963Representation: pubData)
        } catch {
            fatalError("ClipSync: P256 public key import failed: \(error)")
        }

        // 3. Build a temporary signed cert with swift-certificates so we
        //    get a well-formed DER. We sign with a throwaway key here —
        //    we only use this to obtain the TBS byte range. The final
        //    cert is re-signed below with the real SecKey.
        let certPub = Certificate.PublicKey(p256Pub)
        let throwawayPriv = Certificate.PrivateKey(P256.Signing.PrivateKey())
        let name = try! DistinguishedName {
            CommonName("clipsync")
        }
        var serialBytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, serialBytes.count, &serialBytes)
        // High bit must be clear for DER INTEGER positivity.
        serialBytes[0] &= 0x7f
        let serial = Certificate.SerialNumber(bytes: serialBytes)
        let now = Date()
        let tmpCert = try! Certificate(
            version: .v3,
            serialNumber: serial,
            publicKey: certPub,
            notValidBefore: now.addingTimeInterval(-3600),
            notValidAfter: now.addingTimeInterval(20 * 365 * 86400),
            issuer: name,
            subject: name,
            signatureAlgorithm: .ecdsaWithSHA256,
            extensions: Certificate.Extensions(),
            issuerPrivateKey: throwawayPriv
        )
        var ser = DER.Serializer()
        try! ser.serialize(tmpCert)
        let tmpCertDER = Data(ser.serializedBytes)

        // 4. Extract TBS bytes (first child of outer SEQUENCE).
        let tbsBytes = extractFirstSequenceChild(of: tmpCertDER)

        // 5. Re-sign TBS bytes with the real SecKey.
        var signErr: Unmanaged<CFError>?
        guard let sigCF = SecKeyCreateSignature(
            secPrivKey,
            .ecdsaSignatureMessageX962SHA256,
            tbsBytes as CFData,
            &signErr
        ) else {
            fatalError("ClipSync: SecKeyCreateSignature failed: \(signErr!.takeRetainedValue())")
        }
        let signature = sigCF as Data

        // 6. Rebuild the final Certificate DER with the real signature.
        //    Certificate ::= SEQUENCE { tbsCertificate, signatureAlgorithm, signatureValue }
        //    signatureAlgorithm: SEQUENCE { OID 1.2.840.10045.4.3.2 }  (ecdsaWithSHA256)
        let algID: [UInt8] = [0x30, 0x0a, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02]
        let bitStringContent: [UInt8] = [0x00] + [UInt8](signature)
        var bitString: [UInt8] = [0x03]
        bitString.append(contentsOf: derLength(bitStringContent.count))
        bitString.append(contentsOf: bitStringContent)

        var innerBytes = [UInt8](tbsBytes)
        innerBytes.append(contentsOf: algID)
        innerBytes.append(contentsOf: bitString)

        var outerBytes: [UInt8] = [0x30]
        outerBytes.append(contentsOf: derLength(innerBytes.count))
        outerBytes.append(contentsOf: innerBytes)

        let finalCertDER = Data(outerBytes)

        // 7. Convert to SecCertificate and sanity-verify: the cert's
        //    public key must match the keychain-resident private key,
        //    otherwise TLS peers will reject it at handshake time.
        guard let secCert = SecCertificateCreateWithData(nil, finalCertDER as CFData) else {
            fatalError("ClipSync: SecCertificateCreateWithData failed (malformed re-signed DER?)")
        }
        guard let certKey = SecCertificateCopyKey(secCert),
              let certPubData = SecKeyCopyExternalRepresentation(certKey, nil) as Data?,
              certPubData == pubData else {
            fatalError("ClipSync: rebuilt cert public key does not match permanent key")
        }
        storeCertDER(finalCertDER)
        let certAdd: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecValueRef as String: secCert,
            kSecAttrLabel as String: label,
        ]
        let certStatus = SecItemAdd(certAdd as CFDictionary, nil)
        if certStatus != errSecSuccess && certStatus != errSecDuplicateItem {
            NSLog("SecItemAdd(cert) warning: \(certStatus)")
        }

        // 8. Resolve a SecIdentity binding this cert to the permanent key.
        var idOut: SecIdentity?
        let idStatus = SecIdentityCreateWithCertificate(nil, secCert, &idOut)
        if idStatus == errSecSuccess, let id = idOut {
            return (id, secCert)
        }
        // Fallback: query by label.
        let q: [String: Any] = [
            kSecClass as String: kSecClassIdentity,
            kSecAttrLabel as String: label,
            kSecReturnRef as String: true
        ]
        var out: AnyObject?
        let qStatus = SecItemCopyMatching(q as CFDictionary, &out)
        if qStatus == errSecSuccess, let ref = out {
            return (ref as! SecIdentity, secCert)
        }
        fatalError("ClipSync: SecIdentity resolution failed: create=\(idStatus) query=\(qStatus)")
    }
}

// MARK: - Raw DER helpers

/// Encodes a length in DER short or long form.
private func derLength(_ n: Int) -> [UInt8] {
    if n < 0x80 { return [UInt8(n)] }
    var v = n
    var bytes: [UInt8] = []
    while v > 0 {
        bytes.insert(UInt8(v & 0xff), at: 0)
        v >>= 8
    }
    return [0x80 | UInt8(bytes.count)] + bytes
}

/// Reads a DER length starting at `offset`; returns (contentLength, bytesConsumed).
private func readDERLength(_ data: Data, at offset: Int) -> (Int, Int) {
    let first = data[offset]
    if first < 0x80 { return (Int(first), 1) }
    let n = Int(first & 0x7f)
    var len = 0
    for i in 0..<n { len = (len << 8) | Int(data[offset + 1 + i]) }
    return (len, 1 + n)
}

/// Returns the raw DER bytes of the first child SEQUENCE of an outer SEQUENCE.
/// Used to pull the TBSCertificate slice out of a fully-encoded Certificate.
private func extractFirstSequenceChild(of der: Data) -> Data {
    precondition(der[0] == 0x30, "expected outer SEQUENCE")
    let (_, outerLenBytes) = readDERLength(der, at: 1)
    let childStart = 1 + outerLenBytes
    precondition(der[childStart] == 0x30, "expected inner SEQUENCE (TBSCertificate)")
    let (tbsContentLen, tbsLenBytes) = readDERLength(der, at: childStart + 1)
    let tbsEnd = childStart + 1 + tbsLenBytes + tbsContentLen
    return der.subdata(in: childStart..<tbsEnd)
}
