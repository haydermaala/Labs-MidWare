//! Declarative, signed, data-first driver runtime.
//!
//! A driver is a **data** package (manifest + declarative rules + fixtures), not
//! executable code. This crate models and validates driver manifests, verifies
//! signatures, and (in later increments) applies declarative extraction/mapping.
//! Nothing here executes untrusted code; advanced transforms are sandboxed and
//! off by default.
#![forbid(unsafe_code)]

pub mod manifest;

pub use manifest::{
    Capabilities, DriverStatus, FirmwareRange, Manifest, ManifestError, ProtocolFamily, Signature,
    Transport, SUPPORTED_SCHEMA_VERSION,
};

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
