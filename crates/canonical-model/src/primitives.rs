//! Value primitives shared across the model.

use serde::{Deserialize, Serialize};
use time::OffsetDateTime;

/// Exact decimal for clinical numeric values.
///
/// Backed by [`rust_decimal::Decimal`], which stores a scaled integer (no binary
/// floating point) and is configured to serialize as a **string** so exact scale
/// — including trailing zeros — round-trips losslessly. Never represent a
/// clinical numeric result as `f32`/`f64`.
pub type DecimalValue = rust_decimal::Decimal;

/// A UTC instant, serialized as RFC 3339.
///
/// Always stored in UTC. Any source-local offset is normalized on ingestion and,
/// where clinically relevant, the original offset is retained on the owning
/// record rather than by mutating this instant.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub struct Timestamp(#[serde(with = "time::serde::rfc3339")] OffsetDateTime);

impl Timestamp {
    /// The current UTC instant.
    #[must_use]
    pub fn now() -> Self {
        Self(OffsetDateTime::now_utc())
    }

    /// Wrap an [`OffsetDateTime`], normalizing to UTC.
    #[must_use]
    pub fn from_datetime(dt: OffsetDateTime) -> Self {
        Self(dt.to_offset(time::UtcOffset::UTC))
    }

    /// Borrow the underlying UTC datetime.
    #[must_use]
    pub const fn as_datetime(&self) -> OffsetDateTime {
        self.0
    }
}

/// A coded concept: a code drawn from a named coding system, with optional
/// human-readable text. The originating `system`/`code` are preserved verbatim;
/// terminology mapping (e.g. to LOINC/UCUM) happens later and never overwrites
/// the source coding.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Coded {
    /// Identifier of the coding system (opaque string; e.g. a vendor system id).
    pub system: String,
    /// The code within that system, preserved as received.
    pub code: String,
    /// Optional display text as received.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
}

impl Coded {
    /// Construct a coded concept.
    pub fn new(system: impl Into<String>, code: impl Into<String>) -> Self {
        Self {
            system: system.into(),
            code: code.into(),
            text: None,
        }
    }
}

/// A unit of measure, preserved as received (UCUM alignment is a later mapping
/// step and never silently rewrites the source unit).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Unit(pub String);

impl Unit {
    /// Construct a unit from a raw string.
    pub fn new(raw: impl Into<String>) -> Self {
        Self(raw.into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use core::str::FromStr;

    #[test]
    fn decimal_preserves_scale_and_trailing_zeros() {
        // Exactness matters clinically: "1.200" must not become "1.2" or a float.
        let d = DecimalValue::from_str("1.200").unwrap();
        let json = serde_json::to_string(&d).unwrap();
        assert_eq!(
            json, "\"1.200\"",
            "decimal must serialize as an exact string"
        );
        let back: DecimalValue = serde_json::from_str(&json).unwrap();
        assert_eq!(back.to_string(), "1.200");
    }

    #[test]
    fn decimal_high_precision_roundtrips() {
        let d = DecimalValue::from_str("0.0000000001").unwrap();
        let json = serde_json::to_string(&d).unwrap();
        let back: DecimalValue = serde_json::from_str(&json).unwrap();
        assert_eq!(d, back);
    }

    #[test]
    fn timestamp_is_utc_and_roundtrips() {
        let dt = OffsetDateTime::from_unix_timestamp(1_700_000_000).unwrap();
        let ts = Timestamp::from_datetime(dt);
        let json = serde_json::to_string(&ts).unwrap();
        let back: Timestamp = serde_json::from_str(&json).unwrap();
        assert_eq!(ts, back);
        assert_eq!(ts.as_datetime().offset(), time::UtcOffset::UTC);
    }

    #[test]
    fn coded_omits_absent_text() {
        let c = Coded::new("vendor-sys", "GLU");
        assert_eq!(
            serde_json::to_string(&c).unwrap(),
            r#"{"system":"vendor-sys","code":"GLU"}"#
        );
    }
}
