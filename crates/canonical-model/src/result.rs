//! Results, result sets, specimens, and orders.

use serde::{Deserialize, Serialize};

use crate::ids::{LabOrderId, RequestedTestId, ResultId, ResultSetId, SpecimenId};
use crate::primitives::{Coded, DecimalValue, Timestamp, Unit};
use crate::provenance::Provenance;

/// Why a value is absent. Recorded explicitly rather than inferred.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AbsentReason {
    /// The analyzer did not report a value.
    NotReported,
    /// Below the assay's detection limit.
    BelowDetectionLimit,
    /// Above the assay's measuring range.
    AboveDetectionLimit,
    /// Result pending.
    Pending,
    /// Reason unknown / not determinable from the source.
    Unknown,
}

/// The value of a result. A result is exactly one of these variants; they are
/// never coerced into one another (e.g. a non-numeric text result is not forced
/// into a number).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum ResultValue {
    /// An exact numeric value with an optional unit.
    Numeric {
        /// Exact decimal — never a float.
        value: DecimalValue,
        /// Unit as received, if any.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        unit: Option<Unit>,
    },
    /// A coded value (e.g. a qualitative result code).
    Coded {
        /// The coded concept, source coding preserved.
        coded: Coded,
    },
    /// Free text.
    Text {
        /// The text as received.
        text: String,
    },
    /// No value, with an explicit reason.
    Absent {
        /// Why the value is absent.
        reason: AbsentReason,
    },
}

/// Lifecycle status of a result. Never guessed: if the source is ambiguous the
/// status is [`ResultStatus::Unknown`] rather than an assumed `Final`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ResultStatus {
    /// Preliminary/partial result.
    Preliminary,
    /// Final result.
    Final,
    /// A correction to a previously reported result.
    Corrected,
    /// The result/order was cancelled.
    Cancelled,
    /// Source status could not be determined.
    Unknown,
}

/// A flag reported alongside a result (e.g. an abnormal-flag code). Preserved as
/// received; not interpreted into clinical meaning here.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(transparent)]
pub struct ResultFlag(pub String);

/// A reference range as reported by the source.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize)]
pub struct ReferenceRange {
    /// Lower bound (exact decimal), if given.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub low: Option<DecimalValue>,
    /// Upper bound (exact decimal), if given.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub high: Option<DecimalValue>,
    /// Free-text range, if the source expressed it as text.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
}

/// A single normalized result, carrying its provenance chain.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Result {
    /// Identity of this result.
    pub id: ResultId,
    /// The test this result is for, as a coded concept (source coding preserved).
    pub test: Coded,
    /// The value.
    pub value: ResultValue,
    /// Lifecycle status.
    pub status: ResultStatus,
    /// Flags reported with the result.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub flags: Vec<ResultFlag>,
    /// Reference range, if reported.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reference_range: Option<ReferenceRange>,
    /// When the result was observed/produced, if known.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub observed_at: Option<Timestamp>,
    /// Chain of custody — mandatory.
    pub provenance: Provenance,
}

/// A set of results reported together (e.g. one report from an analyzer run).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ResultSet {
    /// Identity of this set.
    pub id: ResultSetId,
    /// The specimen these results pertain to, if identified.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub specimen: Option<SpecimenId>,
    /// The results.
    pub results: Vec<Result>,
}

/// A specimen.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Specimen {
    /// Identity of this specimen.
    pub id: SpecimenId,
    /// Accession/barcode identifier as received, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub accession: Option<String>,
    /// Specimen type, as a coded concept, if provided.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kind: Option<Coded>,
    /// Collection time, if known.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub collected_at: Option<Timestamp>,
}

/// A test requested within an order.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RequestedTest {
    /// Identity of the request line.
    pub id: RequestedTestId,
    /// The test requested, as a coded concept.
    pub test: Coded,
}

/// A laboratory order.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LabOrder {
    /// Identity of this order.
    pub id: LabOrderId,
    /// The specimen this order is for.
    pub specimen: SpecimenId,
    /// The requested tests.
    pub tests: Vec<RequestedTest>,
    /// When the order was placed, if known.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ordered_at: Option<Timestamp>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ids::RawMessageId;
    use core::str::FromStr;

    fn sample_result() -> Result {
        Result {
            id: ResultId::new(),
            test: Coded::new("vendor-sys", "GLU"),
            value: ResultValue::Numeric {
                value: DecimalValue::from_str("5.30").unwrap(),
                unit: Some(Unit::new("mmol/L")),
            },
            status: ResultStatus::Final,
            flags: vec![ResultFlag("H".to_owned())],
            reference_range: Some(ReferenceRange {
                low: Some(DecimalValue::from_str("3.9").unwrap()),
                high: Some(DecimalValue::from_str("5.6").unwrap()),
                text: None,
            }),
            observed_at: Some(Timestamp::now()),
            provenance: Provenance::new(RawMessageId::new(), "astm-engine/0.1.0"),
        }
    }

    #[test]
    fn result_roundtrips_without_loss() {
        let r = sample_result();
        let json = serde_json::to_string(&r).unwrap();
        let back: Result = serde_json::from_str(&json).unwrap();
        assert_eq!(r, back);
    }

    #[test]
    fn numeric_value_keeps_exact_decimal() {
        let r = sample_result();
        let json = serde_json::to_string(&r).unwrap();
        // "5.30" must survive as-is, not collapse to 5.3 or a float.
        assert!(json.contains(r#""value":"5.30""#), "json was: {json}");
    }

    #[test]
    fn result_value_variants_are_tagged_and_distinct() {
        let text = ResultValue::Text {
            text: "see comment".to_owned(),
        };
        let absent = ResultValue::Absent {
            reason: AbsentReason::BelowDetectionLimit,
        };
        assert!(serde_json::to_string(&text)
            .unwrap()
            .contains(r#""kind":"text""#));
        assert!(serde_json::to_string(&absent)
            .unwrap()
            .contains("below_detection_limit"));
    }

    #[test]
    fn result_set_roundtrips() {
        let set = ResultSet {
            id: ResultSetId::new(),
            specimen: Some(SpecimenId::new()),
            results: vec![sample_result()],
        };
        let json = serde_json::to_string(&set).unwrap();
        let back: ResultSet = serde_json::from_str(&json).unwrap();
        assert_eq!(set, back);
    }
}
