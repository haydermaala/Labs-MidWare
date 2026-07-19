//! Driver package signing, verification, and revocation.
//!
//! Driver packages are signed offline (ed25519) and **verified** by the runtime
//! before installation. Verification checks: the declared algorithm, the package
//! digest (SHA-256) against the actual bytes, the signing key against a trust
//! store, key/digest/driver revocation, and finally the signature itself. A
//! tampered package fails digest or signature verification and must be rejected.

use std::collections::{HashMap, HashSet};

use ed25519_dalek::{Signature as EdSignature, Signer, SigningKey, Verifier, VerifyingKey};
use sha2::{Digest, Sha256};
use thiserror::Error;

use crate::manifest::Signature;

/// Compute the package digest as `"sha256:<hex>"`.
#[must_use]
pub fn compute_digest(payload: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(payload);
    format!("sha256:{}", hex::encode(hasher.finalize()))
}

/// A store of trusted public keys, keyed by `key_id`.
#[derive(Debug, Default)]
pub struct TrustStore {
    keys: HashMap<String, VerifyingKey>,
}

impl TrustStore {
    /// An empty trust store.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Trust a verifying key under `key_id`.
    pub fn add_key(&mut self, key_id: impl Into<String>, key: VerifyingKey) {
        self.keys.insert(key_id.into(), key);
    }

    /// Trust a key from 32-byte hex-encoded public key bytes.
    pub fn add_key_hex(
        &mut self,
        key_id: impl Into<String>,
        hex_key: &str,
    ) -> Result<(), VerifyError> {
        let bytes = hex::decode(hex_key).map_err(|_| VerifyError::BadKeyFormat)?;
        let arr: [u8; 32] = bytes.try_into().map_err(|_| VerifyError::BadKeyFormat)?;
        let key = VerifyingKey::from_bytes(&arr).map_err(|_| VerifyError::BadKeyFormat)?;
        self.add_key(key_id, key);
        Ok(())
    }
}

/// A revocation list: revoked signing keys, package digests, and driver ids.
#[derive(Debug, Default)]
pub struct RevocationList {
    keys: HashSet<String>,
    digests: HashSet<String>,
    drivers: HashSet<String>,
}

impl RevocationList {
    /// An empty revocation list.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Revoke a signing key by id.
    pub fn revoke_key(&mut self, key_id: impl Into<String>) {
        self.keys.insert(key_id.into());
    }

    /// Revoke a specific package digest.
    pub fn revoke_digest(&mut self, digest: impl Into<String>) {
        self.digests.insert(digest.into());
    }

    /// Revoke a driver by id.
    pub fn revoke_driver(&mut self, driver_id: impl Into<String>) {
        self.drivers.insert(driver_id.into());
    }

    /// Whether a driver id is revoked.
    #[must_use]
    pub fn is_driver_revoked(&self, driver_id: &str) -> bool {
        self.drivers.contains(driver_id)
    }
}

/// Errors verifying a package signature.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Error)]
pub enum VerifyError {
    /// The declared algorithm is not supported.
    #[error("unsupported signature algorithm")]
    UnsupportedAlgorithm,
    /// The digest in the signature does not match the package bytes.
    #[error("digest mismatch (package tampered)")]
    DigestMismatch,
    /// The signing key is not in the trust store.
    #[error("unknown signing key")]
    UnknownKey,
    /// The signing key has been revoked.
    #[error("signing key revoked")]
    KeyRevoked,
    /// The package digest has been revoked.
    #[error("package digest revoked")]
    DigestRevoked,
    /// The signature or key bytes were malformed.
    #[error("malformed signature")]
    BadSignatureFormat,
    /// A trusted public key was malformed.
    #[error("malformed key")]
    BadKeyFormat,
    /// The signature did not verify against the key and payload.
    #[error("signature verification failed")]
    Invalid,
}

/// Sign `payload` with `signing_key`, producing a [`Signature`] block. Intended
/// for the offline signing tool and tests — the runtime only ever *verifies*.
#[must_use]
pub fn sign(payload: &[u8], signing_key: &SigningKey, key_id: &str) -> Signature {
    let ed_sig = signing_key.sign(payload);
    Signature {
        digest: compute_digest(payload),
        algorithm: "ed25519".to_owned(),
        key_id: key_id.to_owned(),
        value: hex::encode(ed_sig.to_bytes()),
    }
}

/// Verify a signed package: algorithm, digest, trust, revocation, and signature.
pub fn verify(
    payload: &[u8],
    signature: &Signature,
    trust: &TrustStore,
    revocation: &RevocationList,
) -> Result<(), VerifyError> {
    if signature.algorithm != "ed25519" {
        return Err(VerifyError::UnsupportedAlgorithm);
    }
    if revocation.keys.contains(&signature.key_id) {
        return Err(VerifyError::KeyRevoked);
    }
    if compute_digest(payload) != signature.digest {
        return Err(VerifyError::DigestMismatch);
    }
    if revocation.digests.contains(&signature.digest) {
        return Err(VerifyError::DigestRevoked);
    }

    let key = trust
        .keys
        .get(&signature.key_id)
        .ok_or(VerifyError::UnknownKey)?;
    let sig_bytes = hex::decode(&signature.value).map_err(|_| VerifyError::BadSignatureFormat)?;
    let ed_sig =
        EdSignature::from_slice(&sig_bytes).map_err(|_| VerifyError::BadSignatureFormat)?;
    key.verify(payload, &ed_sig)
        .map_err(|_| VerifyError::Invalid)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn keypair() -> (SigningKey, VerifyingKey) {
        // Deterministic key from a fixed seed (no RNG needed).
        let sk = SigningKey::from_bytes(&[7u8; 32]);
        let vk = sk.verifying_key();
        (sk, vk)
    }

    #[test]
    fn digest_is_stable_and_prefixed() {
        assert_eq!(
            compute_digest(b"abc"),
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn sign_then_verify_roundtrips() {
        let (sk, vk) = keypair();
        let payload = b"driver-package-bytes";
        let sig = sign(payload, &sk, "k1");

        let mut trust = TrustStore::new();
        trust.add_key("k1", vk);
        assert!(verify(payload, &sig, &trust, &RevocationList::new()).is_ok());
    }

    #[test]
    fn tampered_payload_fails_on_digest() {
        let (sk, vk) = keypair();
        let sig = sign(b"original", &sk, "k1");
        let mut trust = TrustStore::new();
        trust.add_key("k1", vk);
        assert_eq!(
            verify(b"tampered", &sig, &trust, &RevocationList::new()),
            Err(VerifyError::DigestMismatch)
        );
    }

    #[test]
    fn unknown_key_rejected() {
        let (sk, _vk) = keypair();
        let payload = b"pkg";
        let sig = sign(payload, &sk, "k1");
        // Empty trust store.
        assert_eq!(
            verify(payload, &sig, &TrustStore::new(), &RevocationList::new()),
            Err(VerifyError::UnknownKey)
        );
    }

    #[test]
    fn revoked_key_and_digest_rejected() {
        let (sk, vk) = keypair();
        let payload = b"pkg";
        let sig = sign(payload, &sk, "k1");
        let mut trust = TrustStore::new();
        trust.add_key("k1", vk);

        let mut rev = RevocationList::new();
        rev.revoke_key("k1");
        assert_eq!(
            verify(payload, &sig, &trust, &rev),
            Err(VerifyError::KeyRevoked)
        );

        let mut rev2 = RevocationList::new();
        rev2.revoke_digest(sig.digest.clone());
        assert_eq!(
            verify(payload, &sig, &trust, &rev2),
            Err(VerifyError::DigestRevoked)
        );
    }

    #[test]
    fn wrong_algorithm_and_bad_signature_bytes_rejected() {
        let (sk, vk) = keypair();
        let payload = b"pkg";
        let mut trust = TrustStore::new();
        trust.add_key("k1", vk);

        let mut sig = sign(payload, &sk, "k1");
        sig.algorithm = "rsa".to_owned();
        assert_eq!(
            verify(payload, &sig, &trust, &RevocationList::new()),
            Err(VerifyError::UnsupportedAlgorithm)
        );

        let mut sig2 = sign(payload, &sk, "k1");
        sig2.value = "not-hex!!".to_owned();
        assert_eq!(
            verify(payload, &sig2, &trust, &RevocationList::new()),
            Err(VerifyError::BadSignatureFormat)
        );
    }

    #[test]
    fn signature_from_a_different_key_fails() {
        let (sk, _vk) = keypair();
        let payload = b"pkg";
        let sig = sign(payload, &sk, "k1");
        // Trust a *different* key under the same id.
        let other = SigningKey::from_bytes(&[9u8; 32]).verifying_key();
        let mut trust = TrustStore::new();
        trust.add_key("k1", other);
        assert_eq!(
            verify(payload, &sig, &trust, &RevocationList::new()),
            Err(VerifyError::Invalid)
        );
    }

    #[test]
    fn add_key_hex_roundtrips() {
        let (sk, vk) = keypair();
        let payload = b"pkg";
        let sig = sign(payload, &sk, "k1");
        let mut trust = TrustStore::new();
        trust
            .add_key_hex("k1", &hex::encode(vk.to_bytes()))
            .unwrap();
        assert!(verify(payload, &sig, &trust, &RevocationList::new()).is_ok());
    }
}
