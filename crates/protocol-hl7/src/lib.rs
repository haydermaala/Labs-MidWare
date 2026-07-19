//! HL7 v2 protocol engine.
//!
//! Layers (independently tested):
//! - [`message`] — HL7 v2 message parsing into a lossless structural form (this
//!   increment).
//! - MLLP framing + listener/client — later increment.
//! - ACK generation and supported profiles (ORU/OML/ORM/ACK…) — later increment.
//!
//! Supported versions/profiles are stated explicitly as they land; this crate
//! does **not** claim generic HL7 compatibility. Input is untrusted and parsing
//! never panics on malformed data.
#![forbid(unsafe_code)]

pub mod ack;
pub mod message;
pub mod mllp;

pub use ack::{build_ack, AckCode};
pub use message::{
    parse_message, parse_segment, Component, Delimiters, Field, Hl7Error, Message, Repetition,
    Segment,
};
pub use mllp::{frame as mllp_frame, Decoder as MllpDecoder, MllpError};

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
        assert_eq!(crate_name(), "protocol-hl7");
    }
}
