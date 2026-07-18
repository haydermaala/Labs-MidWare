//! Errors for passive capture.

use thiserror::Error;

/// Errors that can occur while capturing from a transport.
#[derive(Debug, Error)]
pub enum CaptureError {
    /// An I/O error occurred while reading.
    #[error("io: {0}")]
    Io(String),

    /// The read timed out with no data.
    #[error("read timed out")]
    Timeout,

    /// The downstream sink was closed (receiver dropped); capture cannot continue.
    #[error("capture sink closed")]
    SinkClosed,

    /// A configured bound was violated (e.g. a resource limit).
    #[error("bound exceeded: {0}")]
    BoundExceeded(String),
}

/// Convenience result alias.
pub type Result<T> = core::result::Result<T, CaptureError>;
