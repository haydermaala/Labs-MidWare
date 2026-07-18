//! Transport diagnostics counters.
//!
//! Counters are numeric only — they never contain PHI, result values, device
//! payloads, or patient identifiers, so they are safe to expose in metrics.

use std::sync::atomic::{AtomicU64, Ordering};

use serde::Serialize;

/// Thread-safe counters shared between a capture loop and observers.
#[derive(Debug, Default)]
pub struct TransportStats {
    bytes_received: AtomicU64,
    connections: AtomicU64,
    disconnects: AtomicU64,
    oversized_dropped: AtomicU64,
    read_errors: AtomicU64,
}

impl TransportStats {
    /// Record `n` received bytes.
    pub fn add_bytes(&self, n: u64) {
        self.bytes_received.fetch_add(n, Ordering::Relaxed);
    }

    /// Record a new connection.
    pub fn add_connection(&self) {
        self.connections.fetch_add(1, Ordering::Relaxed);
    }

    /// Record a disconnect.
    pub fn add_disconnect(&self) {
        self.disconnects.fetch_add(1, Ordering::Relaxed);
    }

    /// Record an oversized read that was rejected/segmented.
    pub fn add_oversized_dropped(&self) {
        self.oversized_dropped.fetch_add(1, Ordering::Relaxed);
    }

    /// Record a read error.
    pub fn add_read_error(&self) {
        self.read_errors.fetch_add(1, Ordering::Relaxed);
    }

    /// Take a point-in-time snapshot (safe to serialize into metrics/diagnostics).
    #[must_use]
    pub fn snapshot(&self) -> StatsSnapshot {
        StatsSnapshot {
            bytes_received: self.bytes_received.load(Ordering::Relaxed),
            connections: self.connections.load(Ordering::Relaxed),
            disconnects: self.disconnects.load(Ordering::Relaxed),
            oversized_dropped: self.oversized_dropped.load(Ordering::Relaxed),
            read_errors: self.read_errors.load(Ordering::Relaxed),
        }
    }
}

/// An immutable snapshot of [`TransportStats`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct StatsSnapshot {
    /// Total bytes captured.
    pub bytes_received: u64,
    /// Connections accepted/opened.
    pub connections: u64,
    /// Disconnects observed.
    pub disconnects: u64,
    /// Oversized reads rejected or segmented.
    pub oversized_dropped: u64,
    /// Read errors encountered.
    pub read_errors: u64,
}
