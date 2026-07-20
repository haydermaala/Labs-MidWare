//! Continuous gateway daemon: passively capture ASTM sessions from a transport,
//! ingest them into the durable queue, and periodically report liveness + PHI-free
//! telemetry to the control plane.
//!
//! Capture is strictly passive (transport-core contract): bytes flow device →
//! gateway only, never the reverse. A synthetic analyzer streams ASTM sessions
//! over TCP; each session (framed ENQ … EOT) is assembled per peer and pushed
//! through the ingestion pipeline. No message content ever leaves for the cloud —
//! only counts.

use std::collections::HashMap;
use std::time::Duration;

use durable_queue::Store;
use transport_core::CaptureReceiver;

use crate::cloud::{ControlPlaneClient, Enrollment, Telemetry};
use crate::pipeline::{process_session, PipelineConfig};

/// ASTM end-of-transmission byte; terminates a session.
const ASTM_EOT: u8 = 0x04;

/// Accumulates captured bytes per source and yields complete ASTM sessions,
/// delimited by EOT. A partial session (no EOT yet) is retained until it
/// completes, so a session split across capture chunks is reassembled intact.
#[derive(Default)]
pub struct SessionAssembler {
    buffers: HashMap<String, Vec<u8>>,
}

impl SessionAssembler {
    /// Feed a captured chunk from `source`; return every session it completed.
    pub fn push(&mut self, source: &str, bytes: &[u8]) -> Vec<Vec<u8>> {
        let buf = self.buffers.entry(source.to_owned()).or_default();
        buf.extend_from_slice(bytes);

        let mut sessions = Vec::new();
        while let Some(pos) = buf.iter().position(|&b| b == ASTM_EOT) {
            // Drain up to and including the EOT — that is one complete session.
            sessions.push(buf.drain(..=pos).collect());
        }
        sessions
    }
}

/// Drain all currently-available captured chunks, assemble complete sessions, and
/// ingest them into the durable store. Returns the number of messages persisted.
pub fn drain_and_ingest(
    rx: &CaptureReceiver,
    assembler: &mut SessionAssembler,
    store: &mut Store,
) -> usize {
    let mut ingested = 0;
    while let Ok(chunk) = rx.try_recv() {
        for session in assembler.push(&chunk.source, &chunk.bytes) {
            match process_session(store, &session, &chunk.source, &PipelineConfig::default()) {
                Ok(outcomes) => ingested += outcomes.len(),
                Err(e) => eprintln!("ingest failed: {e}"),
            }
        }
    }
    ingested
}

/// One report cycle: drain+ingest whatever has been captured, then report a
/// heartbeat and PHI-free telemetry. Returns how many messages were ingested this
/// cycle. A cloud error is surfaced but does not stop the daemon — the durable
/// queue retains everything for the next cycle.
pub fn report_cycle(
    rx: &CaptureReceiver,
    assembler: &mut SessionAssembler,
    store: &mut Store,
    client: &ControlPlaneClient,
    enrollment: &Enrollment,
) -> usize {
    let ingested = drain_and_ingest(rx, assembler, store);

    if let Err(e) = client.heartbeat(&enrollment.gateway_id, &enrollment.device_credential) {
        eprintln!("heartbeat failed (will retry next cycle): {e}");
        return ingested;
    }
    match Telemetry::from_store(store) {
        Ok(telemetry) => {
            if let Err(e) = client.report_telemetry(
                &enrollment.gateway_id,
                &enrollment.device_credential,
                &telemetry,
            ) {
                eprintln!("telemetry report failed (will retry next cycle): {e}");
            }
        }
        Err(e) => eprintln!("telemetry snapshot failed: {e}"),
    }
    ingested
}

/// The interval to sleep between report cycles (kept here so main and tests agree).
pub fn cycle_interval(secs: u64) -> Duration {
    Duration::from_secs(secs.max(1))
}

#[cfg(test)]
mod tests {
    use super::*;
    use protocol_astm::encode_session;
    use transport_core::{CaptureSink, Captured};

    fn synthetic_session(i: u32) -> Vec<u8> {
        let msg = format!(
            "H|\\^&|||analyzer|||||host||P|1\rP|1||PID-{i}\rO|1|SPEC-{i}||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r"
        );
        encode_session(msg.as_bytes(), 4096)
    }

    #[test]
    fn assembler_yields_complete_sessions_and_holds_partials() {
        let mut asm = SessionAssembler::default();
        let session = synthetic_session(1);
        let split = session.len() / 2;

        // First half completes nothing (no EOT yet).
        assert!(asm.push("tcp:a", &session[..split]).is_empty());
        // Second half completes exactly one session.
        let done = asm.push("tcp:a", &session[split..]);
        assert_eq!(done.len(), 1);
        assert_eq!(done[0], session);
    }

    #[test]
    fn assembler_keeps_sources_separate() {
        let mut asm = SessionAssembler::default();
        let a = synthetic_session(1);
        let b = synthetic_session(2);
        // Interleave two peers; each completes its own session independently.
        assert!(asm.push("tcp:a", &a[..3]).is_empty());
        assert_eq!(asm.push("tcp:b", &b).len(), 1);
        assert_eq!(asm.push("tcp:a", &a[3..]).len(), 1);
    }

    #[test]
    fn drain_and_ingest_persists_captured_sessions() {
        let (sink, rx) = CaptureSink::bounded(16);
        let mut store = Store::open_in_memory().unwrap();
        let mut asm = SessionAssembler::default();

        // Two distinct synthetic sessions arrive as capture chunks.
        for i in 0..2u32 {
            sink.send(Captured {
                bytes: synthetic_session(i),
                received_at: canonical_model::Timestamp::now(),
                source: format!("tcp:peer-{i}"),
            })
            .unwrap();
        }
        drop(sink); // close so try_recv terminates cleanly

        let ingested = drain_and_ingest(&rx, &mut asm, &mut store);
        assert_eq!(ingested, 2);
        assert_eq!(store.raw_message_count().unwrap(), 2);
    }
}
