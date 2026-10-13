import Foundation
import Network
import Security
import Crypto

/// Wraps the gnarly Security.framework bits for building an
/// NWParameters.TLS options object with SPKI-pinned mutual auth.
///
/// NOTE: Network.framework currently prefers SecIdentity for local
/// identity presentation. We generate a P-256 self-signed cert at first
/// launch and use it for TLS (the Ed25519 key in §2 is the *logical*
/// identity used in Hello/enrollment; TLS pinning is against this cert's
/// SPKI). See Identity.swift for details.
enum TLS {
    /// - Parameter onRefuse: called on the main queue with the peer's did
    ///   whenever the verify block turns a connection away. Lets the listener
    ///   surface a device that is knocking but isn't trusted yet.
    static func makeServerOptions(identity: Identity, trustStore: TrustStore,
                                  onRefuse: ((String) -> Void)? = nil) -> NWProtocolTLS.Options {
        let opts = NWProtocolTLS.Options()
        configure(opts.securityProtocolOptions, identity: identity, trustStore: trustStore,
                  server: true, onRefuse: onRefuse)
        return opts
    }

    static func makeClientOptions(identity: Identity, trustStore: TrustStore,
                                  onRefuse: ((String) -> Void)? = nil) -> NWProtocolTLS.Options {
        let opts = NWProtocolTLS.Options()
        configure(opts.securityProtocolOptions, identity: identity, trustStore: trustStore,
                  server: false, onRefuse: onRefuse)
        return opts
    }

    private static func configure(_ sec: sec_protocol_options_t,
                                  identity: Identity,
                                  trustStore: TrustStore,
                                  server: Bool,
                                  onRefuse: ((String) -> Void)?) {
        sec_protocol_options_set_min_tls_protocol_version(sec, .TLSv13)
        sec_protocol_options_set_max_tls_protocol_version(sec, .TLSv13)

        if let secId = sec_identity_create(identity.secIdentity) {
            sec_protocol_options_set_local_identity(sec, secId)
        }

        // Require client certs on the server side.
        if server {
            sec_protocol_options_set_peer_authentication_required(sec, true)
        }

        sec_protocol_options_set_verify_block(sec, { _, secTrust, complete in
            let trust = sec_trust_copy_ref(secTrust).takeRetainedValue()
            // Extract the leaf and check its SPKI against the trust store.
            guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
                  let leaf = chain.first else {
                Log.write("TLS: refused %@ peer — no certificate in the chain",
                          server ? "incoming" : "outgoing")
                complete(false); return
            }
            let spkiHash = TrustStore.spkiSha256(of: leaf)
            let hex = spkiHash.map { String(format: "%02x", $0) }.joined()
            if trustStore.contains(hash: spkiHash) {
                complete(true)
                return
            }
            // Say why, because the only other trace is the connection
            // failing with `-9808: bad certificate format`, and that is
            // Network.framework's name for "the verify block said no", not a
            // malformed certificate. This cert parsed fine; it just isn't one
            // we have trusted. Without this line the log sends you hunting a
            // certificate-encoding bug that does not exist.
            Log.write("TLS: refused %@ peer %@ — certificate is well-formed but not in the trust store",
                      server ? "incoming" : "outgoing", String(hex.prefix(8)))
            onRefuse?(hex)
            complete(false)
        }, .main)
    }
}
