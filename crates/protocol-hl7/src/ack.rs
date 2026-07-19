//! HL7 v2 acknowledgement (`ACK` / `MSA`) generation.
//!
//! Builds an original-mode `ACK` for an inbound message: sending/receiving
//! applications and facilities are swapped, the acknowledgement code is placed in
//! `MSA-1`, and the inbound `MSH-10` message control id is echoed in `MSA-2`. The
//! timestamp and the ACK's own control id are supplied by the caller so the
//! output is deterministic (and testable).

use crate::message::{Message, Segment};

/// Acknowledgement codes (`MSA-1`, original mode).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AckCode {
    /// `AA` — application accept.
    Accept,
    /// `AE` — application error.
    Error,
    /// `AR` — application reject.
    Reject,
}

impl AckCode {
    fn code(self) -> &'static str {
        match self {
            AckCode::Accept => "AA",
            AckCode::Error => "AE",
            AckCode::Reject => "AR",
        }
    }
}

fn field(msh: &Segment, index: usize) -> String {
    msh.field(index).map(|f| f.text()).unwrap_or_default()
}

fn default_if_empty(value: String, fallback: &str) -> String {
    if value.is_empty() {
        fallback.to_owned()
    } else {
        value
    }
}

/// Build an `ACK` message for `inbound`. Returns `None` if the inbound message has
/// no `MSH` segment (which cannot happen for a successfully parsed message).
#[must_use]
pub fn build_ack(
    inbound: &Message,
    code: AckCode,
    ack_control_id: &str,
    timestamp: &str,
) -> Option<Vec<u8>> {
    let msh = inbound.segment("MSH")?;

    // MSH field indices (MSH-1 is the separator, so MSH-N is at split index N-1
    // for N>=2; MSH-3 is index 2, etc.).
    let send_app = field(msh, 2);
    let send_fac = field(msh, 3);
    let recv_app = field(msh, 4);
    let recv_fac = field(msh, 5);
    let processing = default_if_empty(field(msh, 10), "P");
    let version = default_if_empty(field(msh, 11), "2.5.1");
    let control_id = field(msh, 9);

    // Swap sender/receiver for the reply.
    let ack_msh = format!(
        "MSH|^~\\&|{recv_app}|{recv_fac}|{send_app}|{send_fac}|{timestamp}||ACK|{ack_control_id}|{processing}|{version}"
    );
    let msa = format!("MSA|{}|{control_id}", code.code());

    let mut out = ack_msh.into_bytes();
    out.push(b'\r');
    out.extend_from_slice(msa.as_bytes());
    out.push(b'\r');
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::message::parse_message;

    const ORU: &[u8] = b"MSH|^~\\&|ANALYZER|LAB|LIS|HOSP|20260718100000||ORU^R01|MSG00001|P|2.5.1\rOBX|1|NM|^^^GLU||5.30|mmol/L|||||F\r";

    #[test]
    fn builds_accept_ack_with_swapped_routing() {
        let inbound = parse_message(ORU, 4096).unwrap();
        let ack = build_ack(&inbound, AckCode::Accept, "ACK00001", "20260718100005").unwrap();
        let parsed = parse_message(&ack, 4096).unwrap();

        let msh = parsed.segment("MSH").unwrap();
        // Sender/receiver swapped: ack sending app = inbound receiving app (LIS).
        assert_eq!(field(msh, 2), "LIS");
        assert_eq!(field(msh, 3), "HOSP");
        assert_eq!(field(msh, 4), "ANALYZER");
        assert_eq!(field(msh, 5), "LAB");
        // Message type is ACK.
        assert_eq!(msh.field(8).unwrap().text(), "ACK");

        let msa = parsed.segment("MSA").unwrap();
        assert_eq!(msa.field(1).unwrap().text(), "AA");
        // Inbound control id echoed.
        assert_eq!(msa.field(2).unwrap().text(), "MSG00001");
    }

    #[test]
    fn error_and_reject_codes() {
        let inbound = parse_message(ORU, 4096).unwrap();
        for (code, expected) in [(AckCode::Error, "AE"), (AckCode::Reject, "AR")] {
            let ack = build_ack(&inbound, code, "A", "T").unwrap();
            let parsed = parse_message(&ack, 4096).unwrap();
            assert_eq!(
                parsed.segment("MSA").unwrap().field(1).unwrap().text(),
                expected
            );
        }
    }
}
