//! Validation / conformance harness.
//!
//! A **validation case** is a controlled input with a known expected structural
//! outcome. The harness runs the input through the protocol engine and compares
//! actual vs. expected — deterministically, in CI, with **synthetic data only**.
//!
//! This is the software scaffold for the clinical validation lifecycle: during a
//! real analyzer validation, a laboratory supplies the controlled cases and the
//! expected results, and signs off on the comparison. **Passing these software
//! cases does not confer clinical validity** — that requires the authorized
//! laboratory sign-off described in `docs/validation/validation-strategy.md`.
#![forbid(unsafe_code)]

use protocol_astm::{parse_message, RecordKind};

/// The structural outcome extracted from a result record, for comparison.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ObservedResult {
    /// The universal test id field, verbatim (source coding preserved).
    pub test: String,
    /// The result value, verbatim.
    pub value: String,
    /// The units field, verbatim.
    pub unit: String,
    /// The abnormal-flag field, verbatim.
    pub flag: String,
    /// The result-status field, verbatim.
    pub status: String,
}

/// Extract the structural result outcomes from a complete ASTM message.
///
/// This is deliberately verbatim (no clinical interpretation): it exposes exactly
/// what the analyzer sent, so an expected-vs-actual comparison is meaningful.
#[must_use]
pub fn observe_astm(message: &[u8]) -> Vec<ObservedResult> {
    let Ok(parsed) = parse_message(message, 8192) else {
        return Vec::new();
    };
    let field =
        |rec: &protocol_astm::Record, i: usize| rec.field(i).map(|f| f.text()).unwrap_or_default();
    parsed
        .records
        .iter()
        .filter(|r| r.kind == RecordKind::Result)
        .map(|r| ObservedResult {
            // R|seq|universal-test-id|value|units|reference-range|flags|nature|status
            test: field(r, 2),
            value: field(r, 3),
            unit: field(r, 4),
            flag: field(r, 6),
            status: field(r, 8),
        })
        .collect()
}

/// A named validation case: an input and its expected observed results.
pub struct Case {
    /// Human-readable case name.
    pub name: &'static str,
    /// The synthetic ASTM message.
    pub input: &'static [u8],
    /// Expected observed results.
    pub expected: Vec<ObservedResult>,
}

/// Run a case and return `Ok(())` if actual matches expected, else a diff string.
pub fn run_case(case: &Case) -> Result<(), String> {
    let actual = observe_astm(case.input);
    if actual == case.expected {
        Ok(())
    } else {
        Err(format!(
            "case '{}':\n  expected: {:?}\n  actual:   {:?}",
            case.name, case.expected, actual
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn r(test: &str, value: &str, unit: &str, flag: &str, status: &str) -> ObservedResult {
        ObservedResult {
            test: test.to_owned(),
            value: value.to_owned(),
            unit: unit.to_owned(),
            flag: flag.to_owned(),
            status: status.to_owned(),
        }
    }

    // A representative slice of the plan's clinical case matrix, as SYNTHETIC
    // ASTM messages. The remaining cases (dilution, QC, unknown-barcode, timeout,
    // restart) require analyzer-recorded fixtures during clinical validation.
    fn cases() -> Vec<Case> {
        vec![
            Case {
                name: "normal-numeric",
                input: b"H|\\^&|\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r",
                expected: vec![r("^^^GLU", "5.30", "mmol/L", "N", "F")],
            },
            Case {
                name: "abnormal-high-flag",
                input: b"H|\\^&|\rR|1|^^^K|6.2|mmol/L|3.5^5.1|H||F\rL|1|N\r",
                expected: vec![r("^^^K", "6.2", "mmol/L", "H", "F")],
            },
            Case {
                name: "critical-flag",
                input: b"H|\\^&|\rR|1|^^^K|7.8|mmol/L|3.5^5.1|HH||F\rL|1|N\r",
                expected: vec![r("^^^K", "7.8", "mmol/L", "HH", "F")],
            },
            Case {
                name: "text-result",
                input: b"H|\\^&|\rR|1|^^^HIVAB|Non-Reactive|||||F\rL|1|N\r",
                expected: vec![r("^^^HIVAB", "Non-Reactive", "", "", "F")],
            },
            Case {
                name: "corrected-result",
                input: b"H|\\^&|\rR|1|^^^GLU|5.90|mmol/L|3.9^5.6|N||C\rL|1|N\r",
                expected: vec![r("^^^GLU", "5.90", "mmol/L", "N", "C")],
            },
            Case {
                name: "multiple-results",
                input: b"H|\\^&|\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rR|2|^^^K|4.1|mmol/L|3.5^5.1|N||F\rL|1|N\r",
                expected: vec![
                    r("^^^GLU", "5.30", "mmol/L", "N", "F"),
                    r("^^^K", "4.1", "mmol/L", "N", "F"),
                ],
            },
        ]
    }

    #[test]
    fn all_synthetic_cases_match_expected() {
        for case in cases() {
            run_case(&case).unwrap();
        }
    }

    #[test]
    fn exact_decimal_and_flags_are_preserved_verbatim() {
        let observed = observe_astm(b"H|\\^&|\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1\r");
        assert_eq!(observed[0].value, "5.30"); // trailing zero preserved
        assert_eq!(observed[0].flag, "N");
    }

    #[test]
    fn a_mismatch_is_reported_as_a_diff() {
        let bad = Case {
            name: "intentional-mismatch",
            input: b"H|\\^&|\rR|1|^^^GLU|9.99|mmol/L|||F\rL|1\r",
            expected: vec![r("^^^GLU", "5.30", "mmol/L", "", "F")],
        };
        let err = run_case(&bad).unwrap_err();
        assert!(err.contains("expected"));
        assert!(err.contains("actual"));
    }
}
