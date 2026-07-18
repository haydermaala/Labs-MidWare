//! Reconnect backoff with jitter.
//!
//! Timing math only (no clinical values), so `f64` is acceptable here. Jitter is
//! supplied by the caller as a unit value in `[0, 1)` so the policy is
//! deterministic and testable; production supplies a random unit per attempt.

use std::time::Duration;

/// Exponential backoff policy with symmetric jitter, capped at `max`.
#[derive(Debug, Clone, Copy)]
pub struct BackoffPolicy {
    /// Delay before the first retry.
    pub initial: Duration,
    /// Upper bound on the (pre-jitter) delay.
    pub max: Duration,
    /// Growth factor per attempt (e.g. 2.0).
    pub multiplier: f64,
    /// Jitter as a fraction of the delay in `[0, 1]` (e.g. 0.2 => ±20%).
    pub jitter_frac: f64,
}

impl Default for BackoffPolicy {
    fn default() -> Self {
        Self {
            initial: Duration::from_millis(500),
            max: Duration::from_secs(30),
            multiplier: 2.0,
            jitter_frac: 0.2,
        }
    }
}

impl BackoffPolicy {
    /// Compute the delay for a 0-based `attempt`. `jitter_unit` in `[0, 1)` maps to
    /// a symmetric jitter in `[-jitter_frac, +jitter_frac]`; pass `0.5` for none.
    #[must_use]
    pub fn delay_for(&self, attempt: u32, jitter_unit: f64) -> Duration {
        let grown = self.initial.as_secs_f64() * self.multiplier.powi(attempt as i32);
        let capped = grown.min(self.max.as_secs_f64());
        let jitter = capped * self.jitter_frac * (jitter_unit.clamp(0.0, 1.0) * 2.0 - 1.0);
        Duration::from_secs_f64((capped + jitter).max(0.0))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn no_jitter_at_midpoint() {
        let p = BackoffPolicy {
            initial: Duration::from_millis(100),
            max: Duration::from_secs(10),
            multiplier: 2.0,
            jitter_frac: 0.5,
        };
        assert_eq!(p.delay_for(0, 0.5), Duration::from_millis(100));
        assert_eq!(p.delay_for(1, 0.5), Duration::from_millis(200));
        assert_eq!(p.delay_for(2, 0.5), Duration::from_millis(400));
    }

    #[test]
    fn delay_is_capped_at_max() {
        let p = BackoffPolicy {
            initial: Duration::from_secs(1),
            max: Duration::from_secs(5),
            multiplier: 2.0,
            jitter_frac: 0.0,
        };
        assert_eq!(p.delay_for(10, 0.5), Duration::from_secs(5));
    }

    #[test]
    fn jitter_stays_within_bounds() {
        let p = BackoffPolicy {
            initial: Duration::from_secs(1),
            max: Duration::from_secs(100),
            multiplier: 1.0,
            jitter_frac: 0.2,
        };
        // multiplier 1.0 => base always 1s; jitter in ±20% => [0.8s, 1.2s].
        let lo = p.delay_for(3, 0.0);
        let hi = p.delay_for(3, 0.999);
        assert!(lo >= Duration::from_millis(800) && lo <= Duration::from_millis(1000));
        assert!(hi >= Duration::from_millis(1000) && hi <= Duration::from_millis(1200));
    }
}
