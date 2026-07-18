//! Local authenticated gateway API surface (health, status).
//!
//! Phase 1 scaffold: exposes a serializable health payload only. The HTTP/IPC
//! transport and authentication are implemented in Phase 5 (see DEVELOPMENT_PLAN.md).
#![forbid(unsafe_code)]

use serde::{Deserialize, Serialize};

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
