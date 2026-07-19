//! MLLP (Minimal Lower Layer Protocol) framing for HL7 over TCP.
//!
//! An MLLP frame wraps a message: `VT` (`0x0B`) `…message…` `FS` (`0x1C`) `CR`
//! (`0x0D`). The [`Decoder`] extracts complete messages from a byte stream,
//! buffering partial frames across reads and bounding the in-progress message so
//! a hostile or chatty peer can never cause unbounded buffering. It never panics.

use thiserror::Error;

/// Start-of-block (vertical tab).
pub const VT: u8 = 0x0B;
/// End-of-block (file separator).
pub const FS: u8 = 0x1C;
/// Carriage return (frame terminator, after `FS`).
pub const CR: u8 = 0x0D;

/// Wrap an HL7 message in an MLLP frame.
#[must_use]
pub fn frame(message: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(message.len() + 3);
    out.push(VT);
    out.extend_from_slice(message);
    out.push(FS);
    out.push(CR);
    out
}

/// Errors decoding an MLLP stream.
#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum MllpError {
    /// A message (in progress or complete) exceeded the configured maximum.
    #[error("MLLP message exceeds maximum size ({0} bytes)")]
    MessageTooLong(usize),
}

/// A streaming MLLP decoder. Feed it bytes with [`push`]; it returns any complete
/// messages and retains partial frames for the next call.
///
/// [`push`]: Decoder::push
#[derive(Debug)]
pub struct Decoder {
    buf: Vec<u8>,
    max_message_bytes: usize,
}

impl Decoder {
    /// Create a decoder bounding each message to `max_message_bytes`.
    #[must_use]
    pub fn new(max_message_bytes: usize) -> Self {
        Self {
            buf: Vec::new(),
            max_message_bytes,
        }
    }

    /// Bytes currently buffered (partial frame).
    #[must_use]
    pub fn buffered(&self) -> usize {
        self.buf.len()
    }

    /// Append `bytes` and return any complete messages now available.
    pub fn push(&mut self, bytes: &[u8]) -> Result<Vec<Vec<u8>>, MllpError> {
        self.buf.extend_from_slice(bytes);
        let mut messages = Vec::new();

        loop {
            // Locate the start-of-block; discard any leading garbage.
            let Some(start) = self.buf.iter().position(|&b| b == VT) else {
                // No frame start; keep the buffer bounded.
                if self.buf.len() > self.max_message_bytes {
                    self.buf.clear();
                }
                break;
            };
            if start > 0 {
                self.buf.drain(0..start);
            }
            // buf[0] == VT. Find the end-of-block.
            match self.buf.iter().position(|&b| b == FS) {
                Some(fs) => {
                    let message_len = fs.saturating_sub(1);
                    if message_len > self.max_message_bytes {
                        self.buf.drain(0..fs);
                        return Err(MllpError::MessageTooLong(message_len));
                    }
                    match self.buf.get(fs + 1) {
                        Some(&CR) => {
                            let message = self.buf[1..fs].to_vec();
                            self.buf.drain(0..fs + 2);
                            messages.push(message);
                        }
                        Some(_) => {
                            // FS not followed by CR: malformed; drop through FS.
                            self.buf.drain(0..fs + 1);
                        }
                        None => break, // need one more byte (CR)
                    }
                }
                None => {
                    // No end-of-block yet; bound the in-progress message.
                    if self.buf.len().saturating_sub(1) > self.max_message_bytes {
                        let len = self.buf.len();
                        self.buf.clear();
                        return Err(MllpError::MessageTooLong(len));
                    }
                    break;
                }
            }
        }
        Ok(messages)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_wraps_with_vt_fs_cr() {
        let f = frame(b"MSH|^~\\&|");
        assert_eq!(f[0], VT);
        assert_eq!(f[f.len() - 2], FS);
        assert_eq!(f[f.len() - 1], CR);
        assert_eq!(&f[1..f.len() - 2], b"MSH|^~\\&|");
    }

    #[test]
    fn decodes_a_single_frame() {
        let mut d = Decoder::new(4096);
        let out = d.push(&frame(b"hello")).unwrap();
        assert_eq!(out, vec![b"hello".to_vec()]);
        assert_eq!(d.buffered(), 0);
    }

    #[test]
    fn decodes_two_frames_in_one_push() {
        let mut d = Decoder::new(4096);
        let mut bytes = frame(b"one");
        bytes.extend(frame(b"two"));
        let out = d.push(&bytes).unwrap();
        assert_eq!(out, vec![b"one".to_vec(), b"two".to_vec()]);
    }

    #[test]
    fn reassembles_a_frame_split_across_pushes() {
        let mut d = Decoder::new(4096);
        let f = frame(b"MSH|split");
        assert!(d.push(&f[0..4]).unwrap().is_empty());
        assert!(d.push(&f[4..8]).unwrap().is_empty());
        let out = d.push(&f[8..]).unwrap();
        assert_eq!(out, vec![b"MSH|split".to_vec()]);
    }

    #[test]
    fn discards_garbage_before_the_start_block() {
        let mut d = Decoder::new(4096);
        let mut bytes = b"junk-before".to_vec();
        bytes.extend(frame(b"payload"));
        let out = d.push(&bytes).unwrap();
        assert_eq!(out, vec![b"payload".to_vec()]);
    }

    #[test]
    fn oversized_message_errors() {
        let mut d = Decoder::new(4);
        let err = d.push(&frame(b"way too long")).unwrap_err();
        assert!(matches!(err, MllpError::MessageTooLong(_)));
    }

    #[test]
    fn fuzz_smoke_never_panics() {
        let mut state: u64 = 0x1234_5678_9ABC_DEF0;
        let mut next = || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut d = Decoder::new(256);
        for _ in 0..5000 {
            let len = (next() % 64) as usize;
            let bytes: Vec<u8> = (0..len).map(|_| (next() & 0xFF) as u8).collect();
            let _ = d.push(&bytes);
        }
    }
}
