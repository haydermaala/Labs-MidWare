//! Declarative, signed, data-first driver runtime.
//!
//! A driver is a **data** package (manifest + declarative rules + fixtures), not
//! executable code. This crate models and validates driver manifests, verifies
//! signatures, and (in later increments) applies declarative extraction/mapping.
//! Nothing here executes untrusted code; advanced transforms are sandboxed and
//! off by default.
#![forbid(unsafe_code)]

pub mod manifest;
pub mod registry;
pub mod signing;

pub use manifest::{
    Capabilities, DriverStatus, FirmwareRange, Manifest, ManifestError, ProtocolFamily, Signature,
    Transport, SUPPORTED_SCHEMA_VERSION,
};
pub use registry::{InstalledDriver, Registry, RegistryError, RegistryEvent};
pub use signing::{compute_digest, sign, verify, RevocationList, TrustStore, VerifyError};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crate_version_present() {
        assert!(!CRATE_VERSION.is_empty());
    }
}
