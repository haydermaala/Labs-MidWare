//! Declarative, signed, data-first driver runtime.
//!
//! Phase 1 scaffold: this crate compiles with a documented placeholder surface.
//! Product behavior is implemented in later phases per DEVELOPMENT_PLAN.md.
#![forbid(unsafe_code)]

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Returns the crate name; placeholder proving the crate links and tests run.
pub fn crate_name() -> &'static str {
    env!("CARGO_PKG_NAME")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reports_identity() {
        assert!(!CRATE_VERSION.is_empty());
        assert_eq!(crate_name(), "driver-runtime");
    }
}
