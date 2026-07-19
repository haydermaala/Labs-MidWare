//! Outbound delivery: canonical result sets → HL7 `ORU^R01` → MLLP → LIS, with
//! ACK handling, retries, and dead-lettering over the durable outbox.
//!
//! Generation is **structural** (canonical fields → HL7 positions; source coding
//! and exact decimals preserved). No clinical mapping/unit conversion. Delivery
//! is idempotent by construction: the outbox is deduplicated on enqueue, so a
//! message is delivered at most once even across retries and restarts.

use std::net::SocketAddr;
use std::time::Duration;

use canonical_model::{ResultSet, ResultStatus, ResultValue};
use durable_queue::{Store, StoreError};
use protocol_hl7::{send_message, NetError};
use thiserror::Error;
use time::OffsetDateTime;

/// HL7 timestamp `YYYYMMDDHHMMSS` for `now` (UTC).
fn now_hl7() -> String {
    let n = OffsetDateTime::now_utc();
    format!(
        "{:04}{:02}{:02}{:02}{:02}{:02}",
        n.year(),
        u8::from(n.month()),
        n.day(),
        n.hour(),
        n.minute(),
        n.second()
    )
}

fn status_hl7(status: ResultStatus) -> &'static str {
    match status {
        ResultStatus::Final => "F",
        ResultStatus::Preliminary => "P",
        ResultStatus::Corrected => "C",
        ResultStatus::Cancelled => "X",
        ResultStatus::Unknown => "",
    }
}

/// Build an HL7 `ORU^R01` from a canonical result set. Structural only.
#[must_use]
pub fn canonical_to_oru(result_set: &ResultSet, control_id: &str, timestamp: &str) -> Vec<u8> {
    let mut out =
        format!("MSH|^~\\&|GATEWAY|LAB|LIS|HOSP|{timestamp}||ORU^R01|{control_id}|P|2.5.1")
            .into_bytes();

    for (i, result) in result_set.results.iter().enumerate() {
        let seq = i + 1;
        let (value_type, value, unit) = match &result.value {
            ResultValue::Numeric { value, unit } => (
                "NM",
                value.to_string(),
                unit.as_ref().map(|u| u.0.clone()).unwrap_or_default(),
            ),
            ResultValue::Coded { coded } => ("CE", coded.code.clone(), String::new()),
            ResultValue::Text { text } => ("ST", text.clone(), String::new()),
            ResultValue::Absent { .. } => ("", String::new(), String::new()),
        };
        let reference = result
            .reference_range
            .as_ref()
            .map(|r| match (&r.low, &r.high) {
                (Some(lo), Some(hi)) => format!("{lo}-{hi}"),
                _ => r.text.clone().unwrap_or_default(),
            })
            .unwrap_or_default();
        let flags = result
            .flags
            .iter()
            .map(|f| f.0.as_str())
            .collect::<Vec<_>>()
            .join("~");

        // OBX|seq|type|^^^code||value|unit|ref|flags|||status
        let obx = format!(
            "\rOBX|{seq}|{value_type}|^^^{code}||{value}|{unit}|{reference}|{flags}|||{status}",
            code = result.test.code,
            status = status_hl7(result.status),
        );
        out.extend_from_slice(obx.as_bytes());
    }
    out.push(b'\r');
    out
}

/// Report of a delivery pass.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DeliveryReport {
    /// Messages accepted (AA) and marked delivered.
    pub delivered: usize,
    /// Messages moved to the dead-letter table.
    pub dead: usize,
    /// Messages left pending for a later retry.
    pub retried: usize,
}

/// Errors during a delivery pass.
#[derive(Debug, Error)]
pub enum DeliveryError {
    /// Persistence error.
    #[error(transparent)]
    Store(#[from] StoreError),
    /// A stored payload could not be deserialized into a result set.
    #[error("payload decode: {0}")]
    Decode(String),
}

/// Deliver up to `limit` pending outbox items to the LIS at `addr`. Accepted
/// messages are marked delivered; rejects/errors are retried until
/// `max_attempts`, then dead-lettered. Each step is audited.
pub fn deliver_pending(
    store: &mut Store,
    addr: SocketAddr,
    timeout: Duration,
    max_attempts: i64,
    limit: usize,
) -> Result<DeliveryReport, DeliveryError> {
    let pending = store.pending_outbox(limit)?;
    let timestamp = now_hl7();
    let mut report = DeliveryReport {
        delivered: 0,
        dead: 0,
        retried: 0,
    };

    for item in pending {
        let result_set: ResultSet = serde_json::from_slice(&item.payload)
            .map_err(|e| DeliveryError::Decode(e.to_string()))?;
        let oru = canonical_to_oru(&result_set, &item.id, &timestamp);

        let would_exhaust = item.attempts + 1 >= max_attempts;
        match send_message(addr, &oru, timeout, 1 << 20) {
            Ok(ack) if ack.is_accept() => {
                store.record_ack(&item.id, "accepted", None)?;
                store.append_audit("hl7.delivered", None, None)?;
                report.delivered += 1;
            }
            Ok(ack) => {
                store.append_audit("hl7.rejected", None, Some(&ack.code))?;
                if would_exhaust {
                    store.dead_letter(&item.id, &format!("rejected: {}", ack.code))?;
                    report.dead += 1;
                } else {
                    store.record_attempt(&item.id, &format!("nak: {}", ack.code))?;
                    report.retried += 1;
                }
            }
            Err(e) => {
                if would_exhaust {
                    store.dead_letter(&item.id, &delivery_error_string(&e))?;
                    report.dead += 1;
                } else {
                    store.record_attempt(&item.id, &delivery_error_string(&e))?;
                    report.retried += 1;
                }
            }
        }
    }
    Ok(report)
}

fn delivery_error_string(e: &NetError) -> String {
    format!("delivery failed: {e}")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::pipeline::{process_session, PipelineConfig};
    use protocol_astm::encode_session;
    use protocol_hl7::{AckCode, MockLis};

    const ASTM: &[u8] =
        b"H|\\^&|||analyzer||P|1\rP|1||PID-SYNTH\rO|1|SPEC-1||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r";

    fn ingest_one() -> Store {
        let mut store = Store::open_in_memory().unwrap();
        let session = encode_session(ASTM, 4096);
        process_session(
            &mut store,
            &session,
            "sim:normal",
            &PipelineConfig::default(),
        )
        .unwrap();
        assert_eq!(store.outbox_count("pending").unwrap(), 1);
        store
    }

    #[test]
    fn canonical_to_oru_preserves_value_and_status() {
        let store = ingest_one();
        let pending = store.pending_outbox(10).unwrap();
        let rs: ResultSet = serde_json::from_slice(&pending[0].payload).unwrap();
        let oru = canonical_to_oru(&rs, "C1", "20260718100000");
        let text = String::from_utf8_lossy(&oru);
        assert!(text.contains("ORU^R01"));
        assert!(
            text.contains("|5.30|mmol/L|"),
            "value/unit preserved: {text}"
        );
        assert!(text.ends_with("F\r"), "final status: {text}");
    }

    #[test]
    fn end_to_end_ingest_then_deliver_to_mock_lis() {
        let mut store = ingest_one();
        let lis = MockLis::spawn(AckCode::Accept).unwrap();

        let report =
            deliver_pending(&mut store, lis.addr(), Duration::from_secs(5), 3, 10).unwrap();

        assert_eq!(report.delivered, 1);
        assert_eq!(store.outbox_count("delivered").unwrap(), 1);
        assert_eq!(store.outbox_count("pending").unwrap(), 0);
        // A second pass finds nothing pending → no duplicate delivery.
        let again = deliver_pending(&mut store, lis.addr(), Duration::from_secs(5), 3, 10).unwrap();
        assert_eq!(again.delivered, 0);
    }

    #[test]
    fn reject_is_retried_then_dead_lettered() {
        let mut store = ingest_one();
        let lis = MockLis::spawn(AckCode::Reject).unwrap();

        // max_attempts = 2: first pass retries, second pass dead-letters.
        let first = deliver_pending(&mut store, lis.addr(), Duration::from_secs(5), 2, 10).unwrap();
        assert_eq!(first.retried, 1);
        assert_eq!(store.outbox_count("pending").unwrap(), 1);

        let second =
            deliver_pending(&mut store, lis.addr(), Duration::from_secs(5), 2, 10).unwrap();
        assert_eq!(second.dead, 1);
        assert_eq!(store.outbox_count("dead").unwrap(), 1);
        assert_eq!(store.outbox_count("pending").unwrap(), 0);
    }
}
