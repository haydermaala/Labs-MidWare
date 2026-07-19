//! Driver package manifest model and semantic validation.
//!
//! Drivers are **data-first**: a manifest declares identity, compatibility,
//! capabilities, and (later) declarative extraction/mapping rules. There is no
//! executable code in a driver by default; advanced transforms are sandboxed and
//! disabled unless explicitly enabled. See `docs/product/driver-manifest-v0.1.md`.
//!
//! This module models the manifest and validates it — both structurally (via
//! serde) and semantically (the rules a JSON Schema cannot express, e.g. "a
//! certified driver must name exact models and carry a signature").

use serde::{Deserialize, Serialize};
use thiserror::Error;

/// The manifest schema version this runtime understands.
pub const SUPPORTED_SCHEMA_VERSION: &str = "0.1";

/// Protocol family a driver targets.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProtocolFamily {
    /// ASTM E1381/E1394.
    Astm,
    /// HL7 v2.
    Hl7v2,
    /// Configurable delimited.
    Delimited,
    /// Fixed-width.
    FixedWidth,
}

/// A transport a driver can bind to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Transport {
    /// RS-232 / USB virtual COM.
    Serial,
    /// TCP.
    Tcp,
    /// Watched files.
    File,
}

/// Driver lifecycle status.
///
/// `draft → parsed → mapped → validated → certified → deprecated/revoked`.
/// Only `certified` may enter production, and only site-limited.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum DriverStatus {
    /// Manifest exists, unproven.
    Draft,
    /// Parses recorded synthetic samples.
    Parsed,
    /// Canonical mapping defined.
    Mapped,
    /// Passes conformance + fault tests (technical only).
    Validated,
    /// Controlled clinical validation signed off (site-limited).
    Certified,
    /// Withdrawn.
    Deprecated,
    /// Revoked (e.g. tampered/compromised).
    Revoked,
}

impl DriverStatus {
    /// Whether a driver at this status may be used in production.
    #[must_use]
    pub fn is_production_eligible(self) -> bool {
        matches!(self, DriverStatus::Certified)
    }
}

/// Firmware compatibility range (inclusive strings, compared by the caller's
/// policy; `None` means unbounded).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct FirmwareRange {
    /// Minimum firmware, if bounded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min: Option<String>,
    /// Maximum firmware, if bounded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max: Option<String>,
}

/// Declared driver capabilities. Both default to the safe value (`false`).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Capabilities {
    /// Whether the driver may send commands to the device. MUST be separately
    /// approved and capability-allowlisted at runtime even if declared true.
    #[serde(default)]
    pub outbound_to_device: bool,
    /// Whether the driver uses the sandboxed advanced-transform mechanism.
    #[serde(default)]
    pub advanced_transforms: bool,
}

/// Signature block over the package digest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Signature {
    /// Digest of the package (e.g. "sha256:…").
    pub digest: String,
    /// Signature algorithm identifier.
    pub algorithm: String,
    /// Identifier of the signing key.
    pub key_id: String,
    /// The signature bytes, hex-encoded.
    pub value: String,
}

/// A driver package manifest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    /// Manifest schema version (must be [`SUPPORTED_SCHEMA_VERSION`]).
    pub schema_version: String,
    /// Stable driver id (kebab-case slug).
    pub id: String,
    /// Driver package version (semver-ish). Used for upgrade/rollback ordering.
    pub version: String,
    /// Vendor name (may be an `OPEN` placeholder in drafts).
    pub vendor: String,
    /// Device models covered. A certified driver must not use the `*` wildcard.
    pub models: Vec<String>,
    /// Firmware compatibility range.
    #[serde(default)]
    pub firmware_range: FirmwareRange,
    /// Protocol family.
    pub protocol_family: ProtocolFamily,
    /// Transports the driver binds to.
    pub transports: Vec<Transport>,
    /// Workflows supported (e.g. "results-inbound").
    pub workflows: Vec<String>,
    /// Minimum runtime version required (semver-ish string).
    pub minimum_runtime: String,
    /// Lifecycle status.
    pub status: DriverStatus,
    /// Declared capabilities.
    #[serde(default)]
    pub capabilities: Capabilities,
    /// Signature, if signed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<Signature>,
}

/// Errors loading or validating a manifest.
#[derive(Debug, Error)]
pub enum ManifestError {
    /// The manifest JSON could not be parsed.
    #[error("parse: {0}")]
    Parse(String),
    /// The manifest failed semantic validation.
    #[error("invalid manifest: {}", .0.join("; "))]
    Invalid(Vec<String>),
}

impl Manifest {
    /// Parse a manifest from JSON bytes (structural only).
    pub fn from_json(bytes: &[u8]) -> Result<Self, ManifestError> {
        serde_json::from_slice(bytes).map_err(|e| ManifestError::Parse(e.to_string()))
    }

    /// Validate semantic rules that a schema cannot express. Returns all issues
    /// found, not just the first.
    pub fn validate(&self) -> Result<(), ManifestError> {
        let mut issues = Vec::new();

        if self.schema_version != SUPPORTED_SCHEMA_VERSION {
            issues.push(format!(
                "unsupported schemaVersion '{}' (supported: {SUPPORTED_SCHEMA_VERSION})",
                self.schema_version
            ));
        }
        if !is_slug(&self.id) {
            issues.push(format!("id '{}' is not a kebab-case slug", self.id));
        }
        if self.transports.is_empty() {
            issues.push("transports must not be empty".to_owned());
        }
        if self.workflows.is_empty() {
            issues.push("workflows must not be empty".to_owned());
        }
        if self.models.is_empty() {
            issues.push("models must not be empty".to_owned());
        }
        if !looks_like_version(&self.minimum_runtime) {
            issues.push(format!(
                "minimumRuntime '{}' is not a version",
                self.minimum_runtime
            ));
        }
        if !looks_like_version(&self.version) {
            issues.push(format!("version '{}' is not a version", self.version));
        }

        // A certified driver must be specific and signed.
        if self.status == DriverStatus::Certified {
            if self.models.iter().any(|m| m == "*") {
                issues.push("a certified driver must name exact models (no '*')".to_owned());
            }
            if self.signature.is_none() {
                issues.push("a certified driver must carry a signature".to_owned());
            }
            if self.vendor.trim().is_empty() || self.vendor == "OPEN" {
                issues.push("a certified driver must name a vendor".to_owned());
            }
        }

        if issues.is_empty() {
            Ok(())
        } else {
            Err(ManifestError::Invalid(issues))
        }
    }
}

fn is_slug(s: &str) -> bool {
    !s.is_empty()
        && s.chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
        && !s.starts_with('-')
        && !s.ends_with('-')
}

fn looks_like_version(s: &str) -> bool {
    !s.is_empty()
        && s.split('.')
            .all(|part| part.chars().all(|c| c.is_ascii_digit()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn draft_json() -> Vec<u8> {
        br#"{
          "schemaVersion": "0.1",
          "id": "generic-astm",
          "version": "0.1.0",
          "vendor": "OPEN",
          "models": ["*"],
          "protocolFamily": "astm",
          "transports": ["serial", "tcp", "file"],
          "workflows": ["results-inbound"],
          "minimumRuntime": "0.1.0",
          "status": "draft"
        }"#
        .to_vec()
    }

    #[test]
    fn parses_and_validates_a_draft() {
        let m = Manifest::from_json(&draft_json()).unwrap();
        assert_eq!(m.protocol_family, ProtocolFamily::Astm);
        assert!(!m.capabilities.outbound_to_device);
        assert!(m.validate().is_ok());
        assert!(!m.status.is_production_eligible());
    }

    #[test]
    fn defaults_are_safe() {
        let m = Manifest::from_json(&draft_json()).unwrap();
        // Capabilities default to false when omitted.
        assert!(!m.capabilities.advanced_transforms);
        assert!(m.signature.is_none());
    }

    #[test]
    fn certified_requires_specific_models_vendor_and_signature() {
        let mut m = Manifest::from_json(&draft_json()).unwrap();
        m.status = DriverStatus::Certified;
        let err = m.validate().unwrap_err();
        let msg = err.to_string();
        assert!(msg.contains("exact models"), "{msg}");
        assert!(msg.contains("signature"), "{msg}");
        assert!(msg.contains("vendor"), "{msg}");
    }

    #[test]
    fn certified_with_everything_is_valid_and_production_eligible() {
        let mut m = Manifest::from_json(&draft_json()).unwrap();
        m.status = DriverStatus::Certified;
        m.models = vec!["AcmeX-1000".to_owned()];
        m.vendor = "Acme".to_owned();
        m.signature = Some(Signature {
            digest: "sha256:abc".to_owned(),
            algorithm: "ed25519".to_owned(),
            key_id: "k1".to_owned(),
            value: "deadbeef".to_owned(),
        });
        assert!(m.validate().is_ok());
        assert!(m.status.is_production_eligible());
    }

    #[test]
    fn rejects_bad_id_and_version_and_empty_lists() {
        let mut m = Manifest::from_json(&draft_json()).unwrap();
        m.id = "Not A Slug".to_owned();
        m.minimum_runtime = "vX".to_owned();
        m.transports.clear();
        let err = m.validate().unwrap_err();
        let msg = err.to_string();
        assert!(msg.contains("slug"));
        assert!(msg.contains("minimumRuntime"));
        assert!(msg.contains("transports"));
    }

    #[test]
    fn unsupported_schema_version_rejected() {
        let mut m = Manifest::from_json(&draft_json()).unwrap();
        m.schema_version = "9.9".to_owned();
        assert!(m.validate().is_err());
    }
}
