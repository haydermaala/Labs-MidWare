//! ASTM E1381 link-layer state machine (receiver role).
//!
//! This module is **pure decision logic**: given the current state and an event,
//! it computes the next state and the action the receiver *should* take
//! (`ACK`/`NAK`/deliver). It performs no I/O and holds no sockets. Whether an
//! action's bytes are actually transmitted is decided by the transport/capability
//! layer — in passive-capture mode nothing is sent (safety boundary; ADR 0009).
//!
//! Timing (timeouts) is driven by caller-supplied millisecond readings so tests
//! use a **virtual clock** with no sleeps and fully deterministic behavior.
//!
//! The gateway is the *receiver* of analyzer results, so this models the receiver
//! side: it ACKs good in-sequence frames, NAKs bad/out-of-sequence frames, and
//! re-ACKs duplicate retransmissions without re-delivering them.

/// Link-layer state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkState {
    /// No transmission in progress.
    Idle,
    /// A transmission is established; `expected` is the next frame number wanted.
    Established {
        /// The frame number expected next (ASTM numbers run 1..7 then 0, wrapping).
        expected: u8,
    },
}

/// An event fed to the receiver state machine.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkEvent {
    /// `ENQ` received — sender requests to transmit.
    Enq,
    /// A frame was received. `valid` is false if framing/checksum failed.
    Frame {
        /// The frame's sequence number.
        number: u8,
        /// Whether the frame parsed and checksummed correctly.
        valid: bool,
    },
    /// `EOT` received — sender ends the transmission.
    Eot,
}

/// The action the receiver should take in response to an event.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkAction {
    /// Do nothing (e.g. a stray byte while idle).
    None,
    /// Send `ACK`.
    SendAck,
    /// Deliver the current frame's text to the upper layer, then send `ACK`.
    DeliverAndAck,
    /// Send `NAK` (frame invalid or out of sequence; sender should retransmit).
    SendNak,
}

fn next_frame(n: u8) -> u8 {
    if n >= 7 {
        0
    } else {
        n + 1
    }
}

fn prev_frame(n: u8) -> u8 {
    if n == 0 {
        7
    } else {
        n - 1
    }
}

/// Pure transition: `(state, event) -> (next_state, action)`.
#[must_use]
pub fn transition(state: LinkState, event: LinkEvent) -> (LinkState, LinkAction) {
    match (state, event) {
        // Establishment (or re-establishment) always resets to expect frame 1.
        (_, LinkEvent::Enq) => (LinkState::Established { expected: 1 }, LinkAction::SendAck),

        // Stray input while idle is ignored.
        (LinkState::Idle, _) => (LinkState::Idle, LinkAction::None),

        // End of transmission returns to idle.
        (LinkState::Established { .. }, LinkEvent::Eot) => (LinkState::Idle, LinkAction::None),

        // Invalid frame → NAK, keep expecting the same number.
        (LinkState::Established { expected }, LinkEvent::Frame { valid: false, .. }) => {
            (LinkState::Established { expected }, LinkAction::SendNak)
        }

        // Valid frame.
        (
            LinkState::Established { expected },
            LinkEvent::Frame {
                valid: true,
                number,
            },
        ) => {
            if number == expected {
                (
                    LinkState::Established {
                        expected: next_frame(expected),
                    },
                    LinkAction::DeliverAndAck,
                )
            } else if number == prev_frame(expected) {
                // Duplicate retransmission of the last accepted frame: re-ACK, do
                // not deliver again (dedup).
                (LinkState::Established { expected }, LinkAction::SendAck)
            } else {
                // Out of sequence.
                (LinkState::Established { expected }, LinkAction::SendNak)
            }
        }
    }
}

/// A stateful receiver with a timeout, driven by caller-supplied clock readings.
#[derive(Debug)]
pub struct LinkReceiver {
    state: LinkState,
    timeout_ms: u64,
    last_activity_ms: u64,
}

impl LinkReceiver {
    /// Create a receiver that aborts an established transmission after
    /// `timeout_ms` of inactivity.
    #[must_use]
    pub fn new(timeout_ms: u64) -> Self {
        Self {
            state: LinkState::Idle,
            timeout_ms,
            last_activity_ms: 0,
        }
    }

    /// Current state.
    #[must_use]
    pub fn state(&self) -> LinkState {
        self.state
    }

    /// Handle an event observed at `now_ms`, returning the action to take.
    pub fn on_event(&mut self, event: LinkEvent, now_ms: u64) -> LinkAction {
        self.last_activity_ms = now_ms;
        let (next, action) = transition(self.state, event);
        self.state = next;
        action
    }

    /// Check for an inactivity timeout at `now_ms`. If an established transmission
    /// has been idle at least `timeout_ms`, abort it (return to `Idle`) and return
    /// `true`.
    pub fn poll_timeout(&mut self, now_ms: u64) -> bool {
        if matches!(self.state, LinkState::Established { .. })
            && now_ms.saturating_sub(self.last_activity_ms) >= self.timeout_ms
        {
            self.state = LinkState::Idle;
            true
        } else {
            false
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normal_sequence_acks_and_delivers_in_order() {
        let mut rx = LinkReceiver::new(30_000);
        assert_eq!(rx.on_event(LinkEvent::Enq, 0), LinkAction::SendAck);
        assert_eq!(rx.state(), LinkState::Established { expected: 1 });

        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 1,
                    valid: true
                },
                10
            ),
            LinkAction::DeliverAndAck
        );
        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 2,
                    valid: true
                },
                20
            ),
            LinkAction::DeliverAndAck
        );
        assert_eq!(rx.on_event(LinkEvent::Eot, 30), LinkAction::None);
        assert_eq!(rx.state(), LinkState::Idle);
    }

    #[test]
    fn invalid_frame_is_nakd_and_number_not_advanced() {
        let mut rx = LinkReceiver::new(30_000);
        rx.on_event(LinkEvent::Enq, 0);
        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 1,
                    valid: false
                },
                5
            ),
            LinkAction::SendNak
        );
        assert_eq!(rx.state(), LinkState::Established { expected: 1 });
        // Retransmission of the same number now succeeds.
        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 1,
                    valid: true
                },
                6
            ),
            LinkAction::DeliverAndAck
        );
    }

    #[test]
    fn duplicate_frame_is_reackd_not_redelivered() {
        let mut rx = LinkReceiver::new(30_000);
        rx.on_event(LinkEvent::Enq, 0);
        rx.on_event(
            LinkEvent::Frame {
                number: 1,
                valid: true,
            },
            1,
        ); // expected -> 2
           // Sender missed our ACK and resent frame 1: re-ACK, do NOT deliver again.
        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 1,
                    valid: true
                },
                2
            ),
            LinkAction::SendAck
        );
        assert_eq!(rx.state(), LinkState::Established { expected: 2 });
    }

    #[test]
    fn out_of_sequence_frame_is_nakd() {
        let mut rx = LinkReceiver::new(30_000);
        rx.on_event(LinkEvent::Enq, 0);
        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 5,
                    valid: true
                },
                1
            ),
            LinkAction::SendNak
        );
    }

    #[test]
    fn frame_numbers_wrap_after_seven() {
        let mut rx = LinkReceiver::new(30_000);
        rx.on_event(LinkEvent::Enq, 0);
        for (i, n) in [1u8, 2, 3, 4, 5, 6, 7, 0].into_iter().enumerate() {
            assert_eq!(
                rx.on_event(
                    LinkEvent::Frame {
                        number: n,
                        valid: true
                    },
                    (i + 1) as u64
                ),
                LinkAction::DeliverAndAck,
                "frame number {n} should be accepted in sequence"
            );
        }
        // After 0, the next expected is 1 again.
        assert_eq!(rx.state(), LinkState::Established { expected: 1 });
    }

    #[test]
    fn stray_frame_while_idle_is_ignored() {
        let mut rx = LinkReceiver::new(30_000);
        assert_eq!(
            rx.on_event(
                LinkEvent::Frame {
                    number: 1,
                    valid: true
                },
                0
            ),
            LinkAction::None
        );
        assert_eq!(rx.state(), LinkState::Idle);
    }

    #[test]
    fn timeout_aborts_established_transmission_virtual_clock() {
        let mut rx = LinkReceiver::new(10_000);
        rx.on_event(LinkEvent::Enq, 0); // last activity = 0
        assert!(!rx.poll_timeout(9_999), "not yet timed out");
        assert!(rx.poll_timeout(10_000), "should time out at the boundary");
        assert_eq!(rx.state(), LinkState::Idle);
    }

    #[test]
    fn activity_resets_the_timeout() {
        let mut rx = LinkReceiver::new(10_000);
        rx.on_event(LinkEvent::Enq, 0);
        rx.on_event(
            LinkEvent::Frame {
                number: 1,
                valid: true,
            },
            8_000,
        ); // activity resets
        assert!(!rx.poll_timeout(9_000)); // only 1s since last activity
        assert!(rx.poll_timeout(18_000)); // now >10s idle
    }

    #[test]
    fn reenq_restarts_the_sequence() {
        let mut rx = LinkReceiver::new(30_000);
        rx.on_event(LinkEvent::Enq, 0);
        rx.on_event(
            LinkEvent::Frame {
                number: 1,
                valid: true,
            },
            1,
        ); // expected -> 2
        assert_eq!(rx.on_event(LinkEvent::Enq, 2), LinkAction::SendAck);
        assert_eq!(rx.state(), LinkState::Established { expected: 1 });
    }
}
