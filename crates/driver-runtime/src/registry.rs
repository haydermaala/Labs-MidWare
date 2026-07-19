//! Driver installation registry: verify, install, roll back, and audit.
//!
//! Installing a driver **always** verifies its signature against the trust store
//! and revocation list first — a tampered, unsigned, untrusted, or revoked
//! package is rejected and never installed. Each driver id keeps an ordered
//! version history so a bad upgrade can be rolled back to the previous version.
//! Every install/rollback is recorded in an append-only audit log.

use std::collections::HashMap;

use thiserror::Error;

use crate::manifest::{Manifest, ManifestError};
use crate::signing::{compute_digest, verify, RevocationList, TrustStore, VerifyError};

/// A verified, installed driver.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledDriver {
    /// The driver manifest.
    pub manifest: Manifest,
    /// The verified package digest.
    pub digest: String,
}

/// An audit event in the registry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegistryEvent {
    /// Event kind, e.g. "installed", "rolled-back", "rejected".
    pub kind: &'static str,
    /// Driver id.
    pub driver_id: String,
    /// Driver version.
    pub version: String,
    /// Additional non-PHI detail.
    pub detail: String,
}

/// Errors installing or rolling back a driver.
#[derive(Debug, Error)]
pub enum RegistryError {
    /// The manifest failed semantic validation.
    #[error(transparent)]
    Manifest(#[from] ManifestError),
    /// The package signature failed verification.
    #[error("signature: {0}")]
    Verify(VerifyError),
    /// The driver package is unsigned (installation requires a signature).
    #[error("driver package is unsigned")]
    MissingSignature,
    /// The driver id has been revoked.
    #[error("driver revoked")]
    DriverRevoked,
    /// There is no earlier version to roll back to.
    #[error("nothing to roll back for '{0}'")]
    NothingToRollback(String),
}

/// A registry of installed drivers with version history and an audit log.
#[derive(Debug, Default)]
pub struct Registry {
    active: HashMap<String, usize>,
    history: HashMap<String, Vec<InstalledDriver>>,
    events: Vec<RegistryEvent>,
}

impl Registry {
    /// An empty registry.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Verify and install a signed driver package. `payload` is the exact signed
    /// bytes the signature covers. On success the driver becomes the active
    /// version for its id.
    pub fn install(
        &mut self,
        manifest: Manifest,
        payload: &[u8],
        trust: &TrustStore,
        revocation: &RevocationList,
    ) -> Result<(), RegistryError> {
        manifest.validate()?;

        if revocation.is_driver_revoked(&manifest.id) {
            self.reject(&manifest, "driver revoked");
            return Err(RegistryError::DriverRevoked);
        }

        let signature = manifest
            .signature
            .as_ref()
            .ok_or(RegistryError::MissingSignature)
            .inspect_err(|_| self.reject(&manifest, "unsigned"))?;

        verify(payload, signature, trust, revocation).map_err(|e| {
            self.reject(&manifest, "signature verification failed");
            RegistryError::Verify(e)
        })?;

        let digest = compute_digest(payload);
        let id = manifest.id.clone();
        let version = manifest.version.clone();
        let history = self.history.entry(id.clone()).or_default();
        history.push(InstalledDriver {
            manifest,
            digest: digest.clone(),
        });
        let index = history.len() - 1;
        self.active.insert(id.clone(), index);
        self.events.push(RegistryEvent {
            kind: "installed",
            driver_id: id,
            version,
            detail: digest,
        });
        Ok(())
    }

    /// The active installed driver for `id`, if any.
    #[must_use]
    pub fn active(&self, id: &str) -> Option<&InstalledDriver> {
        let index = *self.active.get(id)?;
        self.history.get(id).and_then(|h| h.get(index))
    }

    /// The full version history for `id` (oldest first).
    #[must_use]
    pub fn history(&self, id: &str) -> &[InstalledDriver] {
        self.history.get(id).map(Vec::as_slice).unwrap_or(&[])
    }

    /// Roll back `id` to the previous installed version.
    pub fn rollback(&mut self, id: &str) -> Result<(), RegistryError> {
        let index = self
            .active
            .get(id)
            .copied()
            .filter(|&i| i > 0)
            .ok_or_else(|| RegistryError::NothingToRollback(id.to_owned()))?;
        self.active.insert(id.to_owned(), index - 1);
        let version = self
            .history(id)
            .get(index - 1)
            .map(|d| d.manifest.version.clone())
            .unwrap_or_default();
        self.events.push(RegistryEvent {
            kind: "rolled-back",
            driver_id: id.to_owned(),
            version,
            detail: "rolled back to previous version".to_owned(),
        });
        Ok(())
    }

    /// The append-only audit log.
    #[must_use]
    pub fn events(&self) -> &[RegistryEvent] {
        &self.events
    }

    fn reject(&mut self, manifest: &Manifest, detail: &str) {
        self.events.push(RegistryEvent {
            kind: "rejected",
            driver_id: manifest.id.clone(),
            version: manifest.version.clone(),
            detail: detail.to_owned(),
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::signing::sign;
    use ed25519_dalek::SigningKey;

    fn signed_manifest(version: &str, sk: &SigningKey) -> (Manifest, Vec<u8>) {
        // The payload is a synthetic package byte string; in production it is the
        // canonical package bytes the signature covers.
        let payload = format!("driver:generic-astm:{version}").into_bytes();
        let mut manifest = Manifest::from_json(
            format!(
                r#"{{
                  "schemaVersion": "0.1",
                  "id": "generic-astm",
                  "version": "{version}",
                  "vendor": "OPEN",
                  "models": ["*"],
                  "protocolFamily": "astm",
                  "transports": ["tcp"],
                  "workflows": ["results-inbound"],
                  "minimumRuntime": "0.1.0",
                  "status": "validated"
                }}"#
            )
            .as_bytes(),
        )
        .unwrap();
        manifest.signature = Some(sign(&payload, sk, "k1"));
        (manifest, payload)
    }

    fn trust_for(sk: &SigningKey) -> TrustStore {
        let mut t = TrustStore::new();
        t.add_key("k1", sk.verifying_key());
        t
    }

    #[test]
    fn installs_a_verified_driver() {
        let sk = SigningKey::from_bytes(&[3u8; 32]);
        let (m, payload) = signed_manifest("0.1.0", &sk);
        let mut reg = Registry::new();
        reg.install(m, &payload, &trust_for(&sk), &RevocationList::new())
            .unwrap();
        assert_eq!(
            reg.active("generic-astm").unwrap().manifest.version,
            "0.1.0"
        );
        assert_eq!(reg.events().last().unwrap().kind, "installed");
    }

    #[test]
    fn rejects_tampered_payload() {
        let sk = SigningKey::from_bytes(&[3u8; 32]);
        let (m, _payload) = signed_manifest("0.1.0", &sk);
        let mut reg = Registry::new();
        let err = reg
            .install(
                m,
                b"tampered-bytes",
                &trust_for(&sk),
                &RevocationList::new(),
            )
            .unwrap_err();
        assert!(matches!(
            err,
            RegistryError::Verify(VerifyError::DigestMismatch)
        ));
        assert!(reg.active("generic-astm").is_none());
        assert_eq!(reg.events().last().unwrap().kind, "rejected");
    }

    #[test]
    fn rejects_unsigned_and_revoked() {
        let sk = SigningKey::from_bytes(&[3u8; 32]);
        let (mut m, payload) = signed_manifest("0.1.0", &sk);
        m.signature = None;
        let mut reg = Registry::new();
        assert!(matches!(
            reg.install(m.clone(), &payload, &trust_for(&sk), &RevocationList::new()),
            Err(RegistryError::MissingSignature)
        ));

        let (m2, payload2) = signed_manifest("0.1.0", &sk);
        let mut rev = RevocationList::new();
        rev.revoke_driver("generic-astm");
        assert!(matches!(
            reg.install(m2, &payload2, &trust_for(&sk), &rev),
            Err(RegistryError::DriverRevoked)
        ));
    }

    #[test]
    fn upgrade_then_rollback() {
        let sk = SigningKey::from_bytes(&[3u8; 32]);
        let trust = trust_for(&sk);
        let mut reg = Registry::new();

        let (v1, p1) = signed_manifest("0.1.0", &sk);
        let (v2, p2) = signed_manifest("0.2.0", &sk);
        reg.install(v1, &p1, &trust, &RevocationList::new())
            .unwrap();
        reg.install(v2, &p2, &trust, &RevocationList::new())
            .unwrap();
        assert_eq!(
            reg.active("generic-astm").unwrap().manifest.version,
            "0.2.0"
        );
        assert_eq!(reg.history("generic-astm").len(), 2);

        reg.rollback("generic-astm").unwrap();
        assert_eq!(
            reg.active("generic-astm").unwrap().manifest.version,
            "0.1.0"
        );
        assert_eq!(reg.events().last().unwrap().kind, "rolled-back");

        // No earlier version to roll back to.
        assert!(matches!(
            reg.rollback("generic-astm"),
            Err(RegistryError::NothingToRollback(_))
        ));
    }
}
