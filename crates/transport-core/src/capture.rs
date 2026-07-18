//! The passive capture pipeline.
//!
//! [`capture_reader`] reads bytes from any [`std::io::Read`] source into bounded
//! [`Captured`] chunks and pushes them to a bounded [`CaptureSink`]. This is the
//! shared heart of every transport (serial/TCP/file): each concrete transport
//! only has to produce a `Read` and a source label.
//!
//! # Safety: capture-only
//! There is **no** send/write path in this module or crate. A passive transport
//! can receive and retain bytes but cannot transmit. Outbound-to-device is a
//! separate, capability-gated concern that lives elsewhere and is disabled by
//! default (see the threat model). This is enforced structurally: the types here
//! simply expose no way to write back to the source.

use std::io::{ErrorKind, Read};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender, TrySendError};

use canonical_model::Timestamp;
use serde::Serialize;

use crate::error::{CaptureError, Result};
use crate::stats::TransportStats;

/// Bounds applied to a capture pipeline. All limits are explicit; nothing is
/// unbounded.
#[derive(Debug, Clone, Copy)]
pub struct CaptureConfig {
    /// Maximum bytes read (and therefore emitted) per chunk. Caps memory per read
    /// and provides oversized-message protection.
    pub max_frame_bytes: usize,
    /// Bounded backpressure: capacity of the channel to the consumer.
    pub channel_capacity: usize,
}

impl Default for CaptureConfig {
    fn default() -> Self {
        Self {
            max_frame_bytes: 64 * 1024,
            channel_capacity: 1024,
        }
    }
}

/// A captured chunk of raw bytes plus non-sensitive source metadata.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Captured {
    /// The raw bytes exactly as received (never interpreted here).
    pub bytes: Vec<u8>,
    /// When the chunk was captured (UTC).
    pub received_at: Timestamp,
    /// A non-PHI source label, e.g. "tcp:127.0.0.1:5000" or "file:/inbox/x.dat".
    pub source: String,
}

/// The consumer end of a capture pipeline.
pub type CaptureReceiver = Receiver<Captured>;

/// A bounded sink that a capture loop pushes [`Captured`] chunks into.
///
/// Backpressure is real: when the consumer is behind and the channel is full,
/// [`CaptureSink::send`] blocks, which in turn slows reads — natural flow control
/// rather than unbounded buffering.
#[derive(Clone)]
pub struct CaptureSink {
    tx: SyncSender<Captured>,
}

impl CaptureSink {
    /// Create a bounded sink and its receiver.
    #[must_use]
    pub fn bounded(capacity: usize) -> (Self, CaptureReceiver) {
        let (tx, rx) = sync_channel(capacity);
        (Self { tx }, rx)
    }

    /// Send a chunk, blocking if the channel is full (backpressure). Errors only
    /// if the receiver has been dropped.
    pub fn send(&self, chunk: Captured) -> Result<()> {
        self.tx.send(chunk).map_err(|_| CaptureError::SinkClosed)
    }

    /// Try to send without blocking. Returns `false` if the channel is full.
    pub fn try_send(&self, chunk: Captured) -> Result<bool> {
        match self.tx.try_send(chunk) {
            Ok(()) => Ok(true),
            Err(TrySendError::Full(_)) => Ok(false),
            Err(TrySendError::Disconnected(_)) => Err(CaptureError::SinkClosed),
        }
    }
}

/// Capture bytes from `reader` into bounded chunks until EOF or error.
///
/// Each emitted [`Captured`] holds at most `config.max_frame_bytes` bytes, so a
/// hostile or chatty source can never cause an unbounded single allocation.
/// Returns `Ok(())` on clean EOF; timeouts surface as [`CaptureError::Timeout`]
/// so the caller can apply a reconnect [`crate::BackoffPolicy`].
pub fn capture_reader<R: Read>(
    mut reader: R,
    source: &str,
    config: &CaptureConfig,
    sink: &CaptureSink,
    stats: &TransportStats,
) -> Result<()> {
    let mut buf = vec![0u8; config.max_frame_bytes.max(1)];
    loop {
        match reader.read(&mut buf) {
            Ok(0) => return Ok(()), // clean EOF
            Ok(n) => {
                stats.add_bytes(n as u64);
                let chunk = Captured {
                    bytes: buf[..n].to_vec(),
                    received_at: Timestamp::now(),
                    source: source.to_owned(),
                };
                sink.send(chunk)?;
            }
            Err(e) if e.kind() == ErrorKind::Interrupted => continue,
            Err(e) if e.kind() == ErrorKind::TimedOut || e.kind() == ErrorKind::WouldBlock => {
                stats.add_read_error();
                return Err(CaptureError::Timeout);
            }
            Err(e) => {
                stats.add_read_error();
                return Err(CaptureError::Io(e.to_string()));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn captures_all_bytes_from_reader() {
        let (sink, rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let cfg = CaptureConfig {
            max_frame_bytes: 1024,
            channel_capacity: 16,
        };
        capture_reader(
            Cursor::new(b"H|1|abc".to_vec()),
            "test:1",
            &cfg,
            &sink,
            &stats,
        )
        .unwrap();
        drop(sink);

        let chunks: Vec<_> = rx.iter().collect();
        let all: Vec<u8> = chunks.iter().flat_map(|c| c.bytes.clone()).collect();
        assert_eq!(all, b"H|1|abc");
        assert_eq!(stats.snapshot().bytes_received, 7);
        assert_eq!(chunks[0].source, "test:1");
    }

    #[test]
    fn no_chunk_exceeds_max_frame_bytes() {
        let (sink, rx) = CaptureSink::bounded(64);
        let stats = TransportStats::default();
        let cfg = CaptureConfig {
            max_frame_bytes: 4,
            channel_capacity: 64,
        };
        let data = vec![b'x'; 10];
        capture_reader(Cursor::new(data), "test:2", &cfg, &sink, &stats).unwrap();
        drop(sink);

        let chunks: Vec<_> = rx.iter().collect();
        assert!(
            chunks.iter().all(|c| c.bytes.len() <= 4),
            "oversized chunk leaked"
        );
        let total: usize = chunks.iter().map(|c| c.bytes.len()).sum();
        assert_eq!(total, 10);
        assert_eq!(stats.snapshot().bytes_received, 10);
    }

    #[test]
    fn send_errors_when_receiver_dropped() {
        let (sink, rx) = CaptureSink::bounded(1);
        drop(rx);
        let stats = TransportStats::default();
        let cfg = CaptureConfig {
            max_frame_bytes: 8,
            channel_capacity: 1,
        };
        let err =
            capture_reader(Cursor::new(b"data".to_vec()), "s", &cfg, &sink, &stats).unwrap_err();
        assert!(matches!(err, CaptureError::SinkClosed));
    }

    #[test]
    fn try_send_reports_full_channel_as_backpressure() {
        let (sink, _rx) = CaptureSink::bounded(1);
        let a = Captured {
            bytes: vec![1],
            received_at: Timestamp::now(),
            source: "s".into(),
        };
        let b = a.clone();
        assert!(sink.try_send(a).unwrap()); // fills the single slot
        assert!(!sink.try_send(b).unwrap(), "second send should report full");
    }

    #[test]
    fn timeout_surfaces_for_reconnect() {
        struct TimeoutReader;
        impl Read for TimeoutReader {
            fn read(&mut self, _: &mut [u8]) -> std::io::Result<usize> {
                Err(std::io::Error::from(ErrorKind::TimedOut))
            }
        }
        let (sink, _rx) = CaptureSink::bounded(4);
        let stats = TransportStats::default();
        let cfg = CaptureConfig::default();
        let err = capture_reader(TimeoutReader, "s", &cfg, &sink, &stats).unwrap_err();
        assert!(matches!(err, CaptureError::Timeout));
        assert_eq!(stats.snapshot().read_errors, 1);
    }
}
