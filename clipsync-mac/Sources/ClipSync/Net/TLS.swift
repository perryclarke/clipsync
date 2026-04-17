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
    static func makeServerOptions(identity: Identity, trustStore: TrustStore) -> NWProtocolTLS.Options {
        let opts = NWProtocolTLS.Options()
        configure(opts.securityProtocolOptions, identity: identity, trustStore: trustStore, server: true)
        return opts
    }

    static func makeClientOptions(identity: Identity, trustStore: TrustStore) -> NWProtocolTLS.Options {
        let opts = NWProtocolTLS.Options()
        configure(opts.securityProtocolOptions, identity: identity, trustStore: trustStore, server: false)
        return opts
    }

    private static func configure(_ sec: sec_protocol_options_t,
                                  identity: Identity,
                                  trustStore: TrustStore,
                                  server: Bool) {
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
                complete(false); return
            }
            let spkiHash = TrustStore.spkiSha256(of: leaf)
            complete(trustStore.contains(hash: spkiHash))
        }, .main)
    }
}
