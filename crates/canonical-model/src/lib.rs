//! Canonical laboratory data model and provenance identifiers.
//!
//! This crate defines the normalized representation every device message is
//! mapped into, plus the provenance chain that links each normalized result back
//! to its raw bytes, parser/driver version, mapping version, validation decision,
//! and delivery. See `docs/architecture/canonical-model-v0.1.md`.
//!
//! Safety-relevant invariants encoded here:
//! - Clinical numeric values use exact decimals ([`DecimalValue`]), never floats.
//! - Result status is explicit and never guessed ([`ResultStatus::Unknown`]).
//! - Validation defaults to not-released ([`ValidationDecision::PendingReview`]).
//! - Unknown/foreign data is preserved, not silently dropped.
#![forbid(unsafe_code)]

mod ids;
mod primitives;
mod provenance;
mod result;

pub use ids::*;
pub use primitives::{Coded, DecimalValue, Timestamp, Unit};
pub use provenance::{Provenance, ValidationDecision};
pub use result::{
    AbsentReason, LabOrder, ReferenceRange, RequestedTest, Result, ResultFlag, ResultSet,
    ResultStatus, ResultValue, Specimen,
};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Returns the crate name; retained for provenance/telemetry labelling.
pub fn crate_name() -> &'static str {
    env!("CARGO_PKG_NAME")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reports_identity() {
        assert!(!CRATE_VERSION.is_empty());
        assert_eq!(crate_name(), "canonical-model");
    }
}
