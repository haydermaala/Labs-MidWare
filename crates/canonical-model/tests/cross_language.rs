//! Cross-language contract test: the Rust model must deserialize the SAME shared
//! fixture that the TypeScript types and JSON Schema validate, and preserve exact
//! decimals. This is the round-trip/compatibility check for Phase 2.

use canonical_model::{ResultSet, ResultStatus, ResultValue, ValidationDecision};

const FIXTURE: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../fixtures/canonical/result_set.v0.1.json"
));

#[test]
fn deserializes_shared_fixture_and_preserves_decimal() {
    let rs: ResultSet = serde_json::from_str(FIXTURE).expect("fixture must deserialize");
    assert_eq!(rs.results.len(), 3);

    let first = &rs.results[0];
    assert_eq!(first.status, ResultStatus::Final);
    assert_eq!(
        first.provenance.validation,
        ValidationDecision::PendingReview
    );
    match &first.value {
        ResultValue::Numeric { value, unit } => {
            // Exact decimal, trailing zero preserved — never a float.
            assert_eq!(value.to_string(), "5.30");
            assert_eq!(unit.as_ref().map(|u| u.0.as_str()), Some("mmol/L"));
        }
        other => panic!("expected numeric value, got {other:?}"),
    }

    // The other variants deserialize into the right shapes.
    assert!(matches!(rs.results[1].value, ResultValue::Coded { .. }));
    assert!(matches!(rs.results[2].value, ResultValue::Absent { .. }));
}

#[test]
fn reserializes_and_reparses_stably() {
    let rs: ResultSet = serde_json::from_str(FIXTURE).unwrap();
    let json = serde_json::to_string(&rs).unwrap();
    let back: ResultSet = serde_json::from_str(&json).unwrap();
    assert_eq!(rs, back);
}
