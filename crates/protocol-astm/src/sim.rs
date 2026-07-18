//! Synthetic ASTM simulator: session encoding, stream scanning, and named
//! fault-injection scenarios.
//!
//! All data here is synthetic by construction — the simulator must never carry
//! real patient data. It exists to exercise the framing, link-layer, assembly,
//! and record-parsing layers deterministically (including fault paths) in tests
//! and CI, with no hardware.

use crate::assembly::{Assembled, MessageAssembler};
use crate::framing::{build_frame, parse_frame, Frame, FrameError, FrameType, ENQ, EOT};
use crate::link::{LinkAction, LinkEvent, LinkReceiver};

/// Encode a complete analyzer→gateway session for `message_text`, splitting it
/// into frames of at most `max_frame_text` bytes: `ENQ`, frames (intermediate
/// `ETB`, final `ETX`, numbered 1..7 then 0), then `EOT`.
#[must_use]
pub fn encode_session(message_text: &[u8], max_frame_text: usize) -> Vec<u8> {
    let mut out = vec![ENQ];
    let chunk = max_frame_text.max(1);
    let chunks: Vec<&[u8]> = if message_text.is_empty() {
        vec![&[]]
    } else {
        message_text.chunks(chunk).collect()
    };
    let last = chunks.len() - 1;
    for (i, text) in chunks.iter().enumerate() {
        let frame = Frame {
            number: ((i + 1) % 8) as u8,
            text: text.to_vec(),
            kind: if i == last {
                FrameType::Final
            } else {
                FrameType::Intermediate
            },
        };
        out.extend_from_slice(&build_frame(&frame));
    }
    out.push(EOT);
    out
}

/// A token recognized in a raw ASTM byte stream.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionToken {
    /// `ENQ`.
    Enq,
    /// `EOT`.
    Eot,
    /// A frame (decoded, or the decode error).
    Frame(Result<Frame, FrameError>),
}

/// Scan a raw session byte stream into tokens. Stray bytes are skipped; a frame
/// runs from `STX` to the next `LF`.
#[must_use]
pub fn scan_session(bytes: &[u8], max_frame_text: usize) -> Vec<SessionToken> {
    use crate::framing::{LF, STX};
    let mut tokens = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            ENQ => {
                tokens.push(SessionToken::Enq);
                i += 1;
            }
            EOT => {
                tokens.push(SessionToken::Eot);
                i += 1;
            }
            STX => {
                let end = bytes[i..]
                    .iter()
                    .position(|&b| b == LF)
                    .map_or(bytes.len(), |rel| i + rel + 1);
                tokens.push(SessionToken::Frame(parse_frame(
                    &bytes[i..end],
                    max_frame_text,
                )));
                i = end;
            }
            _ => i += 1,
        }
    }
    tokens
}

/// A named simulator scenario.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scenario {
    /// A single-frame result message, clean.
    Normal,
    /// A result message split across multiple frames.
    MultiFrame,
    /// One frame arrives with a bad checksum, then is retransmitted correctly.
    MalformedChecksum,
    /// The sender missed our ACK and retransmits an already-accepted frame.
    LostAckDuplicate,
    /// A frame is NAK'd then resent (same as malformed-checksum recovery).
    NakRetry,
    /// The sender goes silent mid-transmission; the receiver times out.
    Timeout,
    /// The sender disconnects after an intermediate frame (no final, no EOT).
    Disconnect,
    /// A host-query (`Q`) message.
    HostQuery,
}

impl Scenario {
    /// All scenarios.
    pub const ALL: &'static [Scenario] = &[
        Scenario::Normal,
        Scenario::MultiFrame,
        Scenario::MalformedChecksum,
        Scenario::LostAckDuplicate,
        Scenario::NakRetry,
        Scenario::Timeout,
        Scenario::Disconnect,
        Scenario::HostQuery,
    ];

    /// A short scenario name.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            Scenario::Normal => "normal",
            Scenario::MultiFrame => "multi-frame",
            Scenario::MalformedChecksum => "malformed-checksum",
            Scenario::LostAckDuplicate => "lost-ack-duplicate",
            Scenario::NakRetry => "nak-retry",
            Scenario::Timeout => "timeout",
            Scenario::Disconnect => "disconnect",
            Scenario::HostQuery => "host-query",
        }
    }
}

/// One step in a simulated session.
enum Step {
    Enq,
    /// A frame from the analyzer; `valid` false simulates a checksum failure.
    Frame {
        frame: Frame,
        valid: bool,
    },
    Eot,
    /// Advance the clock by `ms` (drives inactivity timeouts).
    Idle {
        ms: u64,
    },
}

/// The outcome of running a scenario.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScenarioRun {
    /// The receiver actions, in order.
    pub actions: Vec<LinkAction>,
    /// The fully assembled message, if one completed.
    pub delivered: Option<Vec<u8>>,
    /// Whether the receiver aborted on an inactivity timeout.
    pub timed_out: bool,
}

fn final_frame(number: u8, text: &[u8]) -> Frame {
    Frame {
        number,
        text: text.to_vec(),
        kind: FrameType::Final,
    }
}

fn inter_frame(number: u8, text: &[u8]) -> Frame {
    Frame {
        number,
        text: text.to_vec(),
        kind: FrameType::Intermediate,
    }
}

fn steps_for(scenario: Scenario) -> Vec<Step> {
    match scenario {
        Scenario::Normal => vec![
            Step::Enq,
            Step::Frame {
                frame: final_frame(1, b"H|\\^&|\rR|1|^^^GLU|5.30\rL|1\r"),
                valid: true,
            },
            Step::Eot,
        ],
        Scenario::MultiFrame => vec![
            Step::Enq,
            Step::Frame {
                frame: inter_frame(1, b"H|\\^&|\rP|1\r"),
                valid: true,
            },
            Step::Frame {
                frame: final_frame(2, b"R|1|^^^GLU|5.30\rL|1\r"),
                valid: true,
            },
            Step::Eot,
        ],
        Scenario::MalformedChecksum | Scenario::NakRetry => vec![
            Step::Enq,
            Step::Frame {
                frame: final_frame(1, b"H|\\^&|\rR|1|^^^GLU|5.30\rL|1\r"),
                valid: false, // arrives corrupt → NAK
            },
            Step::Frame {
                frame: final_frame(1, b"H|\\^&|\rR|1|^^^GLU|5.30\rL|1\r"),
                valid: true, // retransmitted OK → deliver
            },
            Step::Eot,
        ],
        Scenario::LostAckDuplicate => vec![
            Step::Enq,
            Step::Frame {
                frame: final_frame(1, b"H|\\^&|\rR|1|^^^GLU|5.30\rL|1\r"),
                valid: true, // delivered
            },
            Step::Frame {
                frame: final_frame(1, b"H|\\^&|\rR|1|^^^GLU|5.30\rL|1\r"),
                valid: true, // sender missed ACK, resends → dedup (re-ACK, not delivered)
            },
            Step::Eot,
        ],
        Scenario::Timeout => vec![Step::Enq, Step::Idle { ms: 60_000 }],
        Scenario::Disconnect => vec![
            Step::Enq,
            Step::Frame {
                frame: inter_frame(1, b"H|\\^&|\rP|1\r"),
                valid: true,
            },
            // No final frame, no EOT: the sender vanished.
        ],
        Scenario::HostQuery => vec![
            Step::Enq,
            Step::Frame {
                frame: final_frame(1, b"H|\\^&|\rQ|1|^SPEC-1||ALL\rL|1\r"),
                valid: true,
            },
            Step::Eot,
        ],
    }
}

/// Run a scenario against the link-layer receiver and message assembler,
/// returning the actions taken and any assembled message.
#[must_use]
pub fn run(scenario: Scenario) -> ScenarioRun {
    let mut receiver = LinkReceiver::new(30_000);
    let mut assembler = MessageAssembler::new(64 * 1024);
    let mut actions = Vec::new();
    let mut delivered = None;
    let mut timed_out = false;
    let mut now_ms = 0u64;

    for step in steps_for(scenario) {
        match step {
            Step::Enq => {
                now_ms += 1;
                actions.push(receiver.on_event(LinkEvent::Enq, now_ms));
            }
            Step::Frame { frame, valid } => {
                now_ms += 1;
                let action = receiver.on_event(
                    LinkEvent::Frame {
                        number: frame.number,
                        valid,
                    },
                    now_ms,
                );
                if action == LinkAction::DeliverAndAck {
                    if let Ok(Assembled::Complete(message)) = assembler.push(&frame) {
                        delivered = Some(message);
                    }
                }
                actions.push(action);
            }
            Step::Eot => {
                now_ms += 1;
                actions.push(receiver.on_event(LinkEvent::Eot, now_ms));
            }
            Step::Idle { ms } => {
                now_ms += ms;
                if receiver.poll_timeout(now_ms) {
                    timed_out = true;
                    assembler.reset();
                }
            }
        }
    }

    ScenarioRun {
        actions,
        delivered,
        timed_out,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::records::{parse_message, RecordKind};

    #[test]
    fn wire_roundtrip_encode_scan_assemble() {
        // Encode a message onto the wire, scan it back, drive the receiver +
        // assembler, and recover the exact original message.
        let message = b"H|\\^&|\rP|1\rR|1|^^^GLU|5.30\rL|1\r";
        let wire = encode_session(message, 12); // force multiple frames

        let mut receiver = LinkReceiver::new(30_000);
        let mut assembler = MessageAssembler::new(4096);
        let mut delivered = None;
        let mut now = 0u64;

        for token in scan_session(&wire, 4096) {
            now += 1;
            match token {
                SessionToken::Enq => {
                    receiver.on_event(LinkEvent::Enq, now);
                }
                SessionToken::Eot => {
                    receiver.on_event(LinkEvent::Eot, now);
                }
                SessionToken::Frame(Ok(frame)) => {
                    let action = receiver.on_event(
                        LinkEvent::Frame {
                            number: frame.number,
                            valid: true,
                        },
                        now,
                    );
                    if action == LinkAction::DeliverAndAck {
                        if let Ok(Assembled::Complete(m)) = assembler.push(&frame) {
                            delivered = Some(m);
                        }
                    }
                }
                SessionToken::Frame(Err(e)) => panic!("frame should decode: {e}"),
            }
        }
        assert_eq!(delivered.as_deref(), Some(message.as_slice()));
    }

    #[test]
    fn normal_scenario_delivers_and_parses() {
        let run = run(Scenario::Normal);
        assert_eq!(
            run.actions.first(),
            Some(&LinkAction::SendAck),
            "ENQ should be ACK'd"
        );
        let msg = run.delivered.expect("a message should be delivered");
        let parsed = parse_message(&msg, 4096).unwrap();
        assert_eq!(parsed.records[0].kind, RecordKind::Header);
        assert!(parsed.records.iter().any(|r| r.kind == RecordKind::Result));
    }

    #[test]
    fn multi_frame_scenario_assembles_both_frames() {
        let run = run(Scenario::MultiFrame);
        let msg = run.delivered.expect("assembled message");
        let parsed = parse_message(&msg, 4096).unwrap();
        let kinds: Vec<_> = parsed.records.iter().map(|r| r.kind).collect();
        assert!(kinds.contains(&RecordKind::Patient) && kinds.contains(&RecordKind::Result));
    }

    #[test]
    fn malformed_checksum_is_nakd_then_recovered() {
        let run = run(Scenario::MalformedChecksum);
        assert!(
            run.actions.contains(&LinkAction::SendNak),
            "bad frame → NAK"
        );
        assert!(
            run.actions.contains(&LinkAction::DeliverAndAck),
            "retransmit → deliver"
        );
        assert!(run.delivered.is_some(), "message recovered after retry");
    }

    #[test]
    fn duplicate_frame_delivers_exactly_once() {
        let run = run(Scenario::LostAckDuplicate);
        let deliveries = run
            .actions
            .iter()
            .filter(|a| **a == LinkAction::DeliverAndAck)
            .count();
        assert_eq!(deliveries, 1, "duplicate must not deliver twice");
        // The second (duplicate) frame is re-ACK'd.
        assert!(run.actions.contains(&LinkAction::SendAck));
        assert!(run.delivered.is_some());
    }

    #[test]
    fn timeout_scenario_aborts_without_delivery() {
        let run = run(Scenario::Timeout);
        assert!(run.timed_out, "receiver should time out");
        assert!(run.delivered.is_none());
    }

    #[test]
    fn disconnect_leaves_message_incomplete() {
        let run = run(Scenario::Disconnect);
        assert!(
            run.delivered.is_none(),
            "no final frame → nothing delivered"
        );
    }

    #[test]
    fn host_query_scenario_parses_query_record() {
        let run = run(Scenario::HostQuery);
        let msg = run.delivered.expect("query message delivered");
        let parsed = parse_message(&msg, 4096).unwrap();
        assert!(parsed.records.iter().any(|r| r.kind == RecordKind::Query));
    }

    #[test]
    fn all_scenarios_have_names_and_run_without_panicking() {
        for &scenario in Scenario::ALL {
            assert!(!scenario.name().is_empty());
            let _ = run(scenario);
        }
    }
}
