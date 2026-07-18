//! Local authenticated gateway API surface.
//!
//! Exposes read-only status and redaction-safe metadata to the technician UI over
//! a **loopback**, **bearer-token-authenticated** HTTP endpoint. The request
//! handling is a pure function ([`api::handle`]) independent of the socket, so it
//! is fully testable without binding a port; [`api::serve`] is a thin `tiny_http`
//! adapter. No PHI, payloads, or result values are ever returned.
#![forbid(unsafe_code)]

use serde::{Deserialize, Serialize};

pub mod api;

pub use api::{handle, serve, ApiConfig, ApiError, ApiRequest, ApiResponse};

pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Operating mode of the gateway. Default is passive per the safety boundary:
/// an unreviewed device must never transmit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum OperatingMode {
    /// Receive/retain bytes only; no transmissions. Default for unknown devices.
    #[default]
    PassiveCapture,
    /// Reserved for validated profiles; not reachable in Phase 1.
    Active,
}

/// Minimal health payload. No PHI or result values ever appear here.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Health {
    pub service: &'static str,
    pub version: &'static str,
    pub status: &'static str,
    pub mode: OperatingMode,
}

/// Build the current health snapshot for the gateway daemon.
pub fn health() -> Health {
    Health {
        service: "gatewayd",
        version: CRATE_VERSION,
        status: "ok",
        mode: OperatingMode::default(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_mode_is_passive() {
        assert_eq!(OperatingMode::default(), OperatingMode::PassiveCapture);
        assert_eq!(health().mode, OperatingMode::PassiveCapture);
    }

    #[test]
    fn health_serializes_without_phi() {
        let json = serde_json::to_string(&health()).unwrap();
        assert!(json.contains("passive_capture"));
        assert!(json.contains("gatewayd"));
    }
}
