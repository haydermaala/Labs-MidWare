//! ASTM E1381/E1394 protocol engine.
//!
//! Organized into independently-tested layers:
//! - [`framing`] — E1381 low-level frame encode/decode + checksum (this increment).
//! - Link-layer state machine (ENQ/ACK/NAK/EOT) — separate module, later increment.
//! - E1394 record parsing (H/P/O/R/C/Q/L) into a lossless representation — later.
//!
//! Every layer treats input as untrusted and never panics on malformed data.
#![forbid(unsafe_code)]

pub mod framing;

pub use framing::{
    build_frame, checksum, parse_frame, Frame, FrameError, FrameType, ACK, CR, ENQ, EOT, ETB, ETX,
    LF, NAK, STX,
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
        assert_eq!(crate_name(), "protocol-astm");
    }
}
