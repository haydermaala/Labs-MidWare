//! Provenance: the chain of custody attached to every normalized result.

use serde::{Deserialize, Serialize};

use crate::ids::{
    AcknowledgementId, DeliveryAttemptId, DriverVersionId, MappingVersionId, RawMessageId,
};

/// The validation decision recorded for a result.
///
/// The default is [`ValidationDecision::PendingReview`] — a result is **never**
/// released by default. Release requires a validated profile/mapping and the
/// required approval; nothing here infers clinical acceptability.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ValidationDecision {
    /// Not yet reviewed; not releasable.
    #[default]
    PendingReview,
    /// Held pending resolution (e.g. unresolved mapping); not releasable.
    Held,
    /// Explicitly approved for release by an authorized, validated path.
    Released,
    /// Rejected; must not be released.
    Rejected,
}

impl ValidationDecision {
    /// Whether a result with this decision may be delivered downstream.
    /// Only an explicit [`ValidationDecision::Released`] qualifies.
    #[must_use]
    pub const fn is_releasable(&self) -> bool {
        matches!(self, ValidationDecision::Released)
    }
}

/// Chain of custody linking a normalized result back to its origins and fate.
///
/// Every [`crate::Result`] carries one. The raw message and parser version are
/// mandatory (they always exist by the time a result is normalized); driver,
/// mapping, delivery, and acknowledgement are populated as the pipeline advances.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Provenance {
    /// The raw bytes this result was ultimately derived from.
    pub raw_message: RawMessageId,
    /// Version string of the parser/protocol engine that produced the parse.
    pub parser_version: String,
    /// The driver/profile version applied, once a driver is involved.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub driver_version: Option<DriverVersionId>,
    /// The mapping version applied, once mapping has occurred.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mapping_version: Option<MappingVersionId>,
    /// The validation decision for this result.
    pub validation: ValidationDecision,
    /// The delivery attempt, once a delivery has been made.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub delivery: Option<DeliveryAttemptId>,
    /// The acknowledgement, once one has been received.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub acknowledgement: Option<AcknowledgementId>,
}

impl Provenance {
    /// Begin a provenance chain from a raw message and the parser version.
    /// Validation starts at [`ValidationDecision::PendingReview`].
    pub fn new(raw_message: RawMessageId, parser_version: impl Into<String>) -> Self {
        Self {
            raw_message,
            parser_version: parser_version.into(),
            driver_version: None,
            mapping_version: None,
            validation: ValidationDecision::PendingReview,
            delivery: None,
            acknowledgement: None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_provenance_is_not_releasable() {
        let p = Provenance::new(RawMessageId::new(), "astm-engine/0.1.0");
        assert_eq!(p.validation, ValidationDecision::PendingReview);
        assert!(!p.validation.is_releasable());
    }

    #[test]
    fn only_released_is_releasable() {
        assert!(ValidationDecision::Released.is_releasable());
        for d in [
            ValidationDecision::PendingReview,
            ValidationDecision::Held,
            ValidationDecision::Rejected,
        ] {
            assert!(!d.is_releasable());
        }
    }

    #[test]
    fn provenance_roundtrips_and_omits_empty_links() {
        let p = Provenance::new(RawMessageId::new(), "astm-engine/0.1.0");
        let json = serde_json::to_string(&p).unwrap();
        assert!(!json.contains("driver_version"));
        let back: Provenance = serde_json::from_str(&json).unwrap();
        assert_eq!(p, back);
    }
}
