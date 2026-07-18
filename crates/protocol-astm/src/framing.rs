//! ASTM E1381 low-level framing and checksum.
//!
//! A frame is: `STX` `FN` `text` (`ETB`|`ETX`) `C1` `C2` `CR` `LF`, where `FN` is
//! a single ASCII digit 0–7, `C1C2` are two hex digits of the checksum, and the
//! checksum is the low byte of the sum of every byte from `FN` through the
//! terminator (`ETB`/`ETX`) inclusive — i.e. excluding `STX`, the checksum
//! characters, and `CR`/`LF`.
//!
//! This module is intentionally free of any link-layer or record semantics: it
//! only encodes/decodes a single, already-delimited frame and never panics on
//! malformed input.

use thiserror::Error;

/// Enquiry — sender requests to transmit.
pub const ENQ: u8 = 0x05;
/// Acknowledge.
pub const ACK: u8 = 0x06;
/// Negative acknowledge.
pub const NAK: u8 = 0x15;
/// Start of text (begins a frame).
pub const STX: u8 = 0x02;
/// End of text (final frame terminator).
pub const ETX: u8 = 0x03;
/// End of transmission block (intermediate frame terminator).
pub const ETB: u8 = 0x17;
/// End of transmission.
pub const EOT: u8 = 0x04;
/// Carriage return.
pub const CR: u8 = 0x0D;
/// Line feed.
pub const LF: u8 = 0x0A;

/// Whether a frame is the final frame of a message or an intermediate one.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FrameType {
    /// Terminated by `ETB`; more frames follow.
    Intermediate,
    /// Terminated by `ETX`; last frame of the message.
    Final,
}

impl FrameType {
    fn terminator(self) -> u8 {
        match self {
            FrameType::Intermediate => ETB,
            FrameType::Final => ETX,
        }
    }
}

/// A single decoded ASTM frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    /// Frame sequence number, 0–7 (wraps).
    pub number: u8,
    /// Frame text (record data), exactly as received. May contain an internal
    /// `CR` record separator; control characters are treated as data here.
    pub text: Vec<u8>,
    /// Intermediate (`ETB`) or final (`ETX`).
    pub kind: FrameType,
}

/// Errors decoding a frame. Decoding never panics; malformed input yields one of
/// these.
#[derive(Debug, Clone, PartialEq, Eq, Error)]
pub enum FrameError {
    /// Fewer bytes than the minimum well-formed frame (`STX … CR LF`).
    #[error("frame too short")]
    TooShort,
    /// The frame did not begin with `STX`.
    #[error("missing STX")]
    MissingStx,
    /// The frame did not end with `CR LF`.
    #[error("missing CR/LF trailer")]
    MissingCrLf,
    /// No `ETB`/`ETX` terminator was found where expected.
    #[error("missing ETB/ETX terminator")]
    MissingTerminator,
    /// The frame number byte was not an ASCII digit 0–7.
    #[error("invalid frame number")]
    BadFrameNumber,
    /// The frame text exceeded the configured maximum length.
    #[error("frame text exceeds maximum length")]
    TooLong,
    /// The checksum characters were not valid hex.
    #[error("invalid checksum characters")]
    BadChecksumChars,
    /// The computed checksum did not match the frame's checksum.
    #[error("checksum mismatch: expected {expected:02X}, got {got:02X}")]
    ChecksumMismatch {
        /// Checksum computed over the frame.
        expected: u8,
        /// Checksum carried by the frame.
        got: u8,
    },
}

/// Compute the ASTM checksum (low byte of the running sum) over `region`, which
/// must be the frame-number byte, the text, and the terminator byte.
#[must_use]
pub fn checksum(region: &[u8]) -> u8 {
    region.iter().fold(0u8, |acc, &b| acc.wrapping_add(b))
}

/// Encode a frame into its on-the-wire byte sequence.
#[must_use]
pub fn build_frame(frame: &Frame) -> Vec<u8> {
    let digit = b'0' + (frame.number % 8);
    let term = frame.kind.terminator();

    let mut region = Vec::with_capacity(frame.text.len() + 2);
    region.push(digit);
    region.extend_from_slice(&frame.text);
    region.push(term);

    let sum = checksum(&region);
    let hex = [hex_digit(sum >> 4), hex_digit(sum & 0x0F)];

    let mut out = Vec::with_capacity(region.len() + 5);
    out.push(STX);
    out.extend_from_slice(&region);
    out.extend_from_slice(&hex);
    out.push(CR);
    out.push(LF);
    out
}

/// Decode a single, complete frame (`STX` … `CR LF`). `max_text_len` bounds the
/// text to protect against oversized frames.
pub fn parse_frame(bytes: &[u8], max_text_len: usize) -> Result<Frame, FrameError> {
    // Minimum: STX, FN, terminator, C1, C2, CR, LF = 7 bytes.
    if bytes.len() < 7 {
        return Err(FrameError::TooShort);
    }
    if bytes[0] != STX {
        return Err(FrameError::MissingStx);
    }
    let len = bytes.len();
    if bytes[len - 2] != CR || bytes[len - 1] != LF {
        return Err(FrameError::MissingCrLf);
    }

    let term_idx = len - 5; // terminator sits just before C1 C2 CR LF
    let term = bytes[term_idx];
    let kind = match term {
        ETB => FrameType::Intermediate,
        ETX => FrameType::Final,
        _ => return Err(FrameError::MissingTerminator),
    };

    let digit = bytes[1];
    if !(b'0'..=b'7').contains(&digit) {
        return Err(FrameError::BadFrameNumber);
    }
    let number = digit - b'0';

    let text = &bytes[2..term_idx];
    if text.len() > max_text_len {
        return Err(FrameError::TooLong);
    }

    let got = parse_hex2(bytes[len - 4], bytes[len - 3]).ok_or(FrameError::BadChecksumChars)?;
    let expected = checksum(&bytes[1..=term_idx]);
    if got != expected {
        return Err(FrameError::ChecksumMismatch { expected, got });
    }

    Ok(Frame {
        number,
        text: text.to_vec(),
        kind,
    })
}

fn hex_digit(nibble: u8) -> u8 {
    match nibble {
        0..=9 => b'0' + nibble,
        _ => b'A' + (nibble - 10),
    }
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'A'..=b'F' => Some(b - b'A' + 10),
        b'a'..=b'f' => Some(b - b'a' + 10),
        _ => None,
    }
}

fn parse_hex2(c1: u8, c2: u8) -> Option<u8> {
    Some(hex_val(c1)? * 16 + hex_val(c2)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn checksum_known_vector() {
        // FN '1' + "H|\^&" + ETX  => 0xD8 (hand-computed).
        let mut region = vec![b'1'];
        region.extend_from_slice(b"H|\\^&");
        region.push(ETX);
        assert_eq!(checksum(&region), 0xD8);
    }

    #[test]
    fn build_then_parse_roundtrips() {
        let frame = Frame {
            number: 1,
            text: b"H|\\^&|||analyzer".to_vec(),
            kind: FrameType::Final,
        };
        let wire = build_frame(&frame);
        assert_eq!(wire[0], STX);
        assert_eq!(wire[wire.len() - 2], CR);
        assert_eq!(wire[wire.len() - 1], LF);
        let parsed = parse_frame(&wire, 4096).unwrap();
        assert_eq!(parsed, frame);
    }

    #[test]
    fn intermediate_frames_use_etb() {
        let frame = Frame {
            number: 3,
            text: b"partial".to_vec(),
            kind: FrameType::Intermediate,
        };
        let wire = build_frame(&frame);
        assert_eq!(wire[wire.len() - 5], ETB);
        assert_eq!(parse_frame(&wire, 4096).unwrap(), frame);
    }

    #[test]
    fn text_with_internal_cr_roundtrips() {
        // ASTM records end with an internal CR before the terminator.
        let frame = Frame {
            number: 2,
            text: b"R|1|^^^GLU|5.30\r".to_vec(),
            kind: FrameType::Final,
        };
        let wire = build_frame(&frame);
        assert_eq!(parse_frame(&wire, 4096).unwrap(), frame);
    }

    #[test]
    fn corrupt_checksum_is_detected() {
        let frame = Frame {
            number: 1,
            text: b"data".to_vec(),
            kind: FrameType::Final,
        };
        let mut wire = build_frame(&frame);
        let n = wire.len();
        // Flip a checksum character.
        wire[n - 4] = if wire[n - 4] == b'0' { b'1' } else { b'0' };
        assert!(matches!(
            parse_frame(&wire, 4096),
            Err(FrameError::ChecksumMismatch { .. })
        ));
    }

    #[test]
    fn structural_errors_are_reported() {
        assert_eq!(parse_frame(b"short", 64), Err(FrameError::TooShort));
        assert_eq!(
            parse_frame(b"XY12\x0d\x0a\x00", 64),
            Err(FrameError::MissingStx)
        );

        // Valid-ish frame but missing CR/LF.
        let mut wire = build_frame(&Frame {
            number: 0,
            text: b"x".to_vec(),
            kind: FrameType::Final,
        });
        let n = wire.len();
        wire[n - 1] = b'!';
        assert_eq!(parse_frame(&wire, 64), Err(FrameError::MissingCrLf));
    }

    #[test]
    fn bad_frame_number_rejected() {
        // Build a frame then corrupt the frame-number digit to '9'.
        let mut wire = build_frame(&Frame {
            number: 0,
            text: b"x".to_vec(),
            kind: FrameType::Final,
        });
        wire[1] = b'9';
        assert_eq!(parse_frame(&wire, 64), Err(FrameError::BadFrameNumber));
    }

    #[test]
    fn oversized_text_rejected() {
        let wire = build_frame(&Frame {
            number: 0,
            text: vec![b'x'; 100],
            kind: FrameType::Final,
        });
        assert_eq!(parse_frame(&wire, 10), Err(FrameError::TooLong));
    }

    #[test]
    fn property_roundtrip_over_many_frames() {
        for number in 0u8..8 {
            for len in [0usize, 1, 5, 32, 200] {
                for kind in [FrameType::Intermediate, FrameType::Final] {
                    let text: Vec<u8> = (0..len).map(|i| b'!' + (i as u8 % 90)).collect();
                    let frame = Frame { number, text, kind };
                    let wire = build_frame(&frame);
                    assert_eq!(parse_frame(&wire, 4096).unwrap(), frame);
                }
            }
        }
    }

    #[test]
    fn fuzz_smoke_never_panics() {
        // Deterministic xorshift; feed arbitrary bytes to the parser and assert it
        // returns (Ok or Err) without panicking or looping.
        let mut state: u64 = 0x9E3779B97F4A7C15;
        let mut next = || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        for _ in 0..5000 {
            let len = (next() % 64) as usize;
            let bytes: Vec<u8> = (0..len).map(|_| (next() & 0xFF) as u8).collect();
            let _ = parse_frame(&bytes, 4096);
        }
    }
}
