//! Passive-capture transport contract shared by all transport adapters.
//!
//! This crate defines *how* the gateway captures bytes from a device safely and
//! within bounds, independent of the concrete medium. Serial, TCP, and file
//! transports build on [`capture_reader`] by supplying a [`std::io::Read`] source.
//!
//! # Capture-only by construction
//! Nothing in this crate can transmit to a device. There is no send/write API on
//! the capture path — a passive transport receives and retains bytes only.
//! Outbound-to-device is a separate, capability-gated capability that is disabled
//! by default and lives outside this crate (see `docs/security/threat-model.md`).
//! Unknown devices are always capture-only.
#![forbid(unsafe_code)]

mod backoff;
mod capture;
mod error;
mod stats;

pub use backoff::BackoffPolicy;
pub use capture::{capture_reader, CaptureConfig, CaptureReceiver, CaptureSink, Captured};
pub use error::{CaptureError, Result};
pub use stats::{StatsSnapshot, TransportStats};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// A passive transport source: something that can be captured from, but never
/// written to. Concrete transports (serial/TCP/file) implement this by wiring a
/// byte source into [`capture_reader`].
///
/// The trait deliberately exposes only a capture entry point and a source label —
/// there is no method to send data back, so "capture-only" is a compile-time
/// property, not a runtime check.
pub trait PassiveTransport {
    /// A stable, non-PHI label identifying this source (e.g. "tcp:0.0.0.0:5000").
    fn source_label(&self) -> String;

    /// Run capture to completion (blocking), pushing chunks into `sink`.
    /// Implementations must honor `config`'s bounds and update `stats`.
    fn capture(
        &self,
        config: &CaptureConfig,
        sink: &CaptureSink,
        stats: &TransportStats,
    ) -> Result<()>;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crate_version_is_present() {
        assert!(!CRATE_VERSION.is_empty());
    }

    #[test]
    fn stats_snapshot_is_phi_free_and_serializes() {
        let stats = TransportStats::default();
        stats.add_bytes(10);
        stats.add_connection();
        let json = serde_json::to_string(&stats.snapshot()).unwrap();
        // Only numeric counters — no payloads, no identifiers.
        assert!(json.contains("\"bytes_received\":10"));
        assert!(json.contains("\"connections\":1"));
        assert!(!json.contains("bytes\":["));
    }
}
