//! The gateway ingestion pipeline (synthetic vertical slice).
//!
//! Ties the layers together for one direction: captured ASTM session bytes →
//! link-layer receive → multi-frame assembly → record parse → **structural**
//! normalization to the canonical model → durable persistence with provenance →
//! deduplicated outbox → audit.
//!
//! # Not clinical mapping
//! Normalization here is **structural only**: it extracts ASTM E1394 record
//! fields by their standard positions and preserves the source coding verbatim.
//! It performs no unit conversion, terminology mapping, reference-range
//! interpretation, or result release — every result is stored with
//! `ValidationDecision::PendingReview` (held). Clinical mapping and release
//! require a validated driver/profile and approval (later, gated phases).

use std::str::FromStr;

use canonical_model::{
    AbsentReason, Coded, DecimalValue, Provenance, RawMessageId, ReferenceRange,
    Result as CanonicalResult, ResultFlag, ResultId, ResultSet, ResultSetId, ResultStatus,
    ResultValue, Unit,
};
use durable_queue::{Enqueue, StoreError};
use protocol_astm::sim::SessionToken;
use protocol_astm::{
    parse_message, scan_session, Assembled, LinkAction, LinkEvent, LinkReceiver, Message,
    MessageAssembler, RecordError, RecordKind,
};
use thiserror::Error;

use durable_queue::Store;

const PARSER_VERSION: &str = concat!("astm-engine/", env!("CARGO_PKG_VERSION"));

/// Bounds for the pipeline.
#[derive(Debug, Clone, Copy)]
pub struct PipelineConfig {
    /// Max frame text bytes.
    pub max_frame_text: usize,
    /// Max record bytes.
    pub max_record_len: usize,
    /// Max assembled message bytes.
    pub max_message_bytes: usize,
    /// Link inactivity timeout (ms).
    pub link_timeout_ms: u64,
}

impl Default for PipelineConfig {
    fn default() -> Self {
        Self {
            max_frame_text: 4096,
            max_record_len: 4096,
            max_message_bytes: 64 * 1024,
            link_timeout_ms: 30_000,
        }
    }
}

/// What happened to one complete ASTM message.
#[derive(Debug)]
pub struct MessageOutcome {
    /// The stored raw message id (provenance root).
    pub raw_id: RawMessageId,
    /// The stored normalized result set id.
    pub result_set_id: ResultSetId,
    /// Number of normalized results.
    pub results: usize,
    /// Whether this was newly enqueued or deduplicated.
    pub enqueue: Enqueue,
}

/// Pipeline errors.
#[derive(Debug, Error)]
pub enum PipelineError {
    /// Persistence error.
    #[error(transparent)]
    Store(#[from] StoreError),
    /// Record parse error.
    #[error("record parse: {0}")]
    Records(#[from] RecordError),
    /// Serialization error.
    #[error("serialize: {0}")]
    Serialize(String),
}

/// Deterministic idempotency key from message content (stable across restarts).
fn content_key(bytes: &[u8]) -> String {
    use std::hash::{Hash, Hasher};
    let mut h = std::hash::DefaultHasher::new();
    bytes.hash(&mut h);
    format!("astm:{:016x}", h.finish())
}

fn field_text(record: &protocol_astm::Record, index: usize) -> Option<String> {
    record
        .field(index)
        .map(protocol_astm::Field::text)
        .filter(|s| !s.is_empty())
}

/// Map an ASTM result-status code to a canonical status. Unknown/ambiguous codes
/// become [`ResultStatus::Unknown`] — status is never guessed to `Final`.
fn map_status(code: Option<&str>) -> ResultStatus {
    match code {
        Some("F") => ResultStatus::Final,
        Some("P") => ResultStatus::Preliminary,
        Some("C") => ResultStatus::Corrected,
        Some("X") => ResultStatus::Cancelled,
        _ => ResultStatus::Unknown,
    }
}

fn parse_reference_range(raw: &str) -> ReferenceRange {
    let mut parts = raw.splitn(2, '^');
    let low = parts.next().and_then(|p| DecimalValue::from_str(p).ok());
    let high = parts.next().and_then(|p| DecimalValue::from_str(p).ok());
    if low.is_some() || high.is_some() {
        ReferenceRange {
            low,
            high,
            text: None,
        }
    } else {
        ReferenceRange {
            low: None,
            high: None,
            text: Some(raw.to_owned()),
        }
    }
}

/// Structurally normalize an ASTM message into a canonical result set. No clinical
/// interpretation; results are held pending review.
#[must_use]
pub fn normalize(message: &Message, raw_id: RawMessageId) -> ResultSet {
    let mut results = Vec::new();
    for record in &message.records {
        if record.kind != RecordKind::Result {
            continue;
        }
        // R|seq|universal-test-id|value|units|reference-range|abnormal-flags|nature|status
        let test_code = field_text(record, 2).unwrap_or_default();
        let value = match field_text(record, 3) {
            Some(raw) => match DecimalValue::from_str(&raw) {
                Ok(decimal) => ResultValue::Numeric {
                    value: decimal,
                    unit: field_text(record, 4).map(Unit::new),
                },
                Err(_) => ResultValue::Text { text: raw },
            },
            None => ResultValue::Absent {
                reason: AbsentReason::NotReported,
            },
        };
        let flags = field_text(record, 6)
            .map(|f| vec![ResultFlag(f)])
            .unwrap_or_default();
        let reference_range = field_text(record, 5).map(|r| parse_reference_range(&r));

        results.push(CanonicalResult {
            id: ResultId::new(),
            test: Coded::new("astm-e1394/test-id", test_code),
            value,
            status: map_status(field_text(record, 8).as_deref()),
            flags,
            reference_range,
            observed_at: None,
            provenance: Provenance::new(raw_id, PARSER_VERSION),
        });
    }

    ResultSet {
        id: ResultSetId::new(),
        specimen: None,
        results,
    }
}

/// Process one complete, assembled ASTM message: persist raw, normalize, persist
/// the result set (linked to raw = provenance), and enqueue for delivery with
/// content-based deduplication. Each step is audited.
pub fn process_message(
    store: &mut Store,
    message_bytes: &[u8],
    source: &str,
    config: &PipelineConfig,
) -> Result<MessageOutcome, PipelineError> {
    // 1. Persist raw evidence first (persist-before-process).
    let raw_id = store.insert_raw_message("astm", message_bytes, Some(source), None)?;
    store.append_audit("astm.captured", Some(source), None)?;

    // 2. Parse + structurally normalize (no clinical mapping).
    let parsed = parse_message(message_bytes, config.max_record_len)?;
    let result_set = normalize(&parsed, raw_id);
    let results = result_set.results.len();
    let payload =
        serde_json::to_vec(&result_set).map_err(|e| PipelineError::Serialize(e.to_string()))?;

    // 3. Persist the normalized result set, linked to its raw message (provenance).
    let result_set_id = store.insert_result_set(raw_id, &payload)?;
    store.append_audit("astm.normalized", Some(source), None)?;

    // 4. Enqueue for delivery, deduplicated by message content.
    let enqueue = store.enqueue(&content_key(message_bytes), Some(result_set_id), &payload)?;
    store.append_audit(
        match enqueue {
            Enqueue::Inserted(_) => "astm.queued",
            Enqueue::Duplicate(_) => "astm.duplicate_suppressed",
        },
        Some(source),
        None,
    )?;

    Ok(MessageOutcome {
        raw_id,
        result_set_id,
        results,
        enqueue,
    })
}

/// Process a full captured ASTM session (ENQ + frames + EOT), driving the
/// link-layer receiver and multi-frame assembler and processing each completed
/// message. Returns one outcome per completed message.
pub fn process_session(
    store: &mut Store,
    session: &[u8],
    source: &str,
    config: &PipelineConfig,
) -> Result<Vec<MessageOutcome>, PipelineError> {
    let mut receiver = LinkReceiver::new(config.link_timeout_ms);
    let mut assembler = MessageAssembler::new(config.max_message_bytes);
    let mut outcomes = Vec::new();
    let mut now = 0u64;

    for token in scan_session(session, config.max_frame_text) {
        now += 1;
        match token {
            SessionToken::Enq => {
                receiver.on_event(LinkEvent::Enq, now);
            }
            SessionToken::Eot => {
                receiver.on_event(LinkEvent::Eot, now);
                assembler.reset();
            }
            SessionToken::Frame(Ok(frame)) => {
                let action = receiver.on_event(
                    LinkEvent::Frame {
                        number: frame.number,
                        valid: true,
                    },
                    now,
                );
                if action == LinkAction::DeliverAndAck {
                    if let Ok(Assembled::Complete(message)) = assembler.push(&frame) {
                        outcomes.push(process_message(store, &message, source, config)?);
                    }
                }
            }
            SessionToken::Frame(Err(_)) => {
                // Malformed frame → the receiver NAKs (number is ignored for NAK).
                receiver.on_event(
                    LinkEvent::Frame {
                        number: 0,
                        valid: false,
                    },
                    now,
                );
            }
        }
    }
    Ok(outcomes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use protocol_astm::encode_session;
    use std::sync::atomic::{AtomicU64, Ordering};

    const MESSAGE: &[u8] =
        b"H|\\^&|||analyzer|||||host||P|1\rP|1||PID-SYNTH\rO|1|SPEC-1||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r";

    fn temp_store_path() -> std::path::PathBuf {
        static N: AtomicU64 = AtomicU64::new(0);
        let dir = std::env::temp_dir().join(format!(
            "gatewayd-pipe-{}-{}",
            std::process::id(),
            N.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir_all(&dir).unwrap();
        dir.join("edge.db")
    }

    #[test]
    fn normalize_is_structural_and_held() {
        let parsed = parse_message(MESSAGE, 4096).unwrap();
        let rs = normalize(&parsed, RawMessageId::new());
        assert_eq!(rs.results.len(), 1);
        let r = &rs.results[0];
        // Value preserved exactly as an exact decimal, unit verbatim.
        match &r.value {
            ResultValue::Numeric { value, unit } => {
                assert_eq!(value.to_string(), "5.30");
                assert_eq!(unit.as_ref().map(|u| u.0.as_str()), Some("mmol/L"));
            }
            other => panic!("expected numeric, got {other:?}"),
        }
        assert_eq!(r.status, ResultStatus::Final);
        // Held, not released — no clinical decision made.
        assert_eq!(
            r.provenance.validation,
            canonical_model::ValidationDecision::PendingReview
        );
    }

    #[test]
    fn full_session_traverses_and_persists_with_provenance() {
        let mut store = Store::open(temp_store_path()).unwrap();
        let session = encode_session(MESSAGE, 24); // multi-frame
        let cfg = PipelineConfig::default();

        let outcomes = process_session(&mut store, &session, "sim:normal", &cfg).unwrap();
        assert_eq!(outcomes.len(), 1);
        assert_eq!(outcomes[0].results, 1);
        assert!(matches!(outcomes[0].enqueue, Enqueue::Inserted(_)));

        // Raw evidence stored; result set linked to it (provenance FK).
        let raw = store.get_raw_message(outcomes[0].raw_id).unwrap();
        assert!(raw.is_some());
        assert_eq!(store.outbox_count("pending").unwrap(), 1);
        // captured + normalized + queued audit events.
        assert!(store.audit_count().unwrap() >= 3);
    }

    #[test]
    fn duplicate_session_does_not_double_deliver() {
        let mut store = Store::open(temp_store_path()).unwrap();
        let session = encode_session(MESSAGE, 4096);
        let cfg = PipelineConfig::default();

        process_session(&mut store, &session, "sim:normal", &cfg).unwrap();
        let second = process_session(&mut store, &session, "sim:normal", &cfg).unwrap();

        assert!(matches!(second[0].enqueue, Enqueue::Duplicate(_)));
        // Still only one queued delivery despite receiving the message twice.
        assert_eq!(store.outbox_count("pending").unwrap(), 1);
    }

    #[test]
    fn data_survives_reopen() {
        let path = temp_store_path();
        let session = encode_session(MESSAGE, 4096);
        let cfg = PipelineConfig::default();
        {
            let mut store = Store::open(&path).unwrap();
            process_session(&mut store, &session, "sim:normal", &cfg).unwrap();
        }
        // Reopen the same file-backed store: the queued delivery is still there.
        let store = Store::open(&path).unwrap();
        assert_eq!(store.outbox_count("pending").unwrap(), 1);
    }
}
