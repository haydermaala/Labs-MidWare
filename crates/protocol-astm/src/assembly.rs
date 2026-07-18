//! Multi-frame message assembly.
//!
//! ASTM messages may span multiple frames: intermediate frames end with `ETB`
//! and the final frame ends with `ETX`. The link layer delivers accepted,
//! in-sequence frames (deduplicated); this assembler concatenates their text into
//! the complete message, bounded by a maximum message size.

use thiserror::Error;

use crate::framing::{Frame, FrameType};

/// Result of pushing a frame into the assembler.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Assembled {
    /// More frames are expected (an `ETB` intermediate frame was pushed).
    Incomplete,
    /// The message is complete (an `ETX` final frame was pushed); the full text
    /// is returned.
    Complete(Vec<u8>),
}

/// Errors during assembly.
#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum AssemblyError {
    /// The assembled message would exceed the configured maximum size.
    #[error("assembled message exceeds maximum size ({0} bytes)")]
    MessageTooLong(usize),
}

/// Accumulates frame text into a complete message.
#[derive(Debug)]
pub struct MessageAssembler {
    buf: Vec<u8>,
    max_message_bytes: usize,
}

impl MessageAssembler {
    /// Create an assembler bounded to `max_message_bytes`.
    #[must_use]
    pub fn new(max_message_bytes: usize) -> Self {
        Self {
            buf: Vec::new(),
            max_message_bytes,
        }
    }

    /// Bytes accumulated so far (across intermediate frames).
    #[must_use]
    pub fn pending_len(&self) -> usize {
        self.buf.len()
    }

    /// Discard any partial message (e.g. after a timeout/abort).
    pub fn reset(&mut self) {
        self.buf.clear();
    }

    /// Append a delivered frame's text. Returns [`Assembled::Complete`] with the
    /// full message when a final (`ETX`) frame is pushed, otherwise
    /// [`Assembled::Incomplete`].
    pub fn push(&mut self, frame: &Frame) -> Result<Assembled, AssemblyError> {
        let projected = self.buf.len() + frame.text.len();
        if projected > self.max_message_bytes {
            return Err(AssemblyError::MessageTooLong(projected));
        }
        self.buf.extend_from_slice(&frame.text);
        match frame.kind {
            FrameType::Intermediate => Ok(Assembled::Incomplete),
            FrameType::Final => {
                let message = std::mem::take(&mut self.buf);
                Ok(Assembled::Complete(message))
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(number: u8, text: &[u8], kind: FrameType) -> Frame {
        Frame {
            number,
            text: text.to_vec(),
            kind,
        }
    }

    #[test]
    fn single_final_frame_completes_immediately() {
        let mut a = MessageAssembler::new(4096);
        let out = a
            .push(&frame(1, b"H|\\^&|\rL|1\r", FrameType::Final))
            .unwrap();
        assert_eq!(out, Assembled::Complete(b"H|\\^&|\rL|1\r".to_vec()));
    }

    #[test]
    fn intermediate_frames_are_concatenated() {
        let mut a = MessageAssembler::new(4096);
        assert_eq!(
            a.push(&frame(1, b"H|\\^&|\rP|1\r", FrameType::Intermediate))
                .unwrap(),
            Assembled::Incomplete
        );
        assert_eq!(a.pending_len(), 11);
        let out = a
            .push(&frame(2, b"R|1|^^^GLU|5.30\rL|1\r", FrameType::Final))
            .unwrap();
        assert_eq!(
            out,
            Assembled::Complete(b"H|\\^&|\rP|1\rR|1|^^^GLU|5.30\rL|1\r".to_vec())
        );
    }

    #[test]
    fn oversized_message_is_rejected() {
        let mut a = MessageAssembler::new(8);
        assert!(matches!(
            a.push(&frame(1, b"way too long", FrameType::Final)),
            Err(AssemblyError::MessageTooLong(_))
        ));
    }

    #[test]
    fn reset_discards_partial_message() {
        let mut a = MessageAssembler::new(4096);
        a.push(&frame(1, b"partial", FrameType::Intermediate))
            .unwrap();
        assert_eq!(a.pending_len(), 7);
        a.reset();
        assert_eq!(a.pending_len(), 0);
    }
}
