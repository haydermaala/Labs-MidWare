//! MLLP-over-TCP client and a mock LIS server (for tests and demos).
//!
//! The client sends one framed HL7 message and reads back the framed `ACK`,
//! returning the `MSA-1` code. The mock LIS accepts one connection, decodes one
//! message, and replies with an `ACK` built from it — enough to exercise the full
//! outbound path end-to-end without a real LIS. All data is synthetic.

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use thiserror::Error;

use crate::ack::{build_ack, AckCode};
use crate::message::parse_message;
use crate::mllp::{frame, Decoder, MllpError};

/// The outcome of delivering a message: the raw ACK and its `MSA-1` code.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeliveryAck {
    /// Raw ACK message bytes.
    pub raw: Vec<u8>,
    /// The `MSA-1` acknowledgement code (e.g. "AA", "AE", "AR"), or empty if the
    /// ACK could not be parsed.
    pub code: String,
}

impl DeliveryAck {
    /// Whether the ACK is an application accept (`AA`).
    #[must_use]
    pub fn is_accept(&self) -> bool {
        self.code == "AA"
    }
}

/// Errors delivering an HL7 message over MLLP.
#[derive(Debug, Error)]
pub enum NetError {
    /// I/O error.
    #[error("io: {0}")]
    Io(String),
    /// The peer closed the connection before sending a complete ACK.
    #[error("connection closed before ACK")]
    ClosedBeforeAck,
    /// MLLP decoding error.
    #[error(transparent)]
    Mllp(#[from] MllpError),
}

fn parse_ack_code(ack: &[u8], max: usize) -> String {
    parse_message(ack, max)
        .ok()
        .and_then(|m| m.segment("MSA").and_then(|s| s.field(1).map(|f| f.text())))
        .unwrap_or_default()
}

/// Connect to an MLLP endpoint, send `message`, and return the ACK.
pub fn send_message(
    addr: SocketAddr,
    message: &[u8],
    timeout: Duration,
    max_message_bytes: usize,
) -> Result<DeliveryAck, NetError> {
    let mut stream =
        TcpStream::connect_timeout(&addr, timeout).map_err(|e| NetError::Io(e.to_string()))?;
    stream
        .set_read_timeout(Some(timeout))
        .map_err(|e| NetError::Io(e.to_string()))?;
    stream
        .write_all(&frame(message))
        .map_err(|e| NetError::Io(e.to_string()))?;

    let mut decoder = Decoder::new(max_message_bytes);
    let mut buf = [0u8; 4096];
    loop {
        let n = stream
            .read(&mut buf)
            .map_err(|e| NetError::Io(e.to_string()))?;
        if n == 0 {
            return Err(NetError::ClosedBeforeAck);
        }
        if let Some(ack) = decoder.push(&buf[..n])?.into_iter().next() {
            let code = parse_ack_code(&ack, max_message_bytes);
            return Ok(DeliveryAck { raw: ack, code });
        }
    }
}

/// A running in-process mock LIS that ACKs one message per connection.
pub struct MockLis {
    addr: SocketAddr,
    handle: Option<JoinHandle<()>>,
}

impl MockLis {
    /// Bind a mock LIS on loopback that replies to each connection's first
    /// message with an ACK carrying `ack_code`. Serves connections until dropped.
    pub fn spawn(ack_code: AckCode) -> std::io::Result<Self> {
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))?;
        let addr = listener.local_addr()?;
        listener.set_nonblocking(false)?;
        let handle = thread::spawn(move || {
            for incoming in listener.incoming() {
                let Ok(mut stream) = incoming else { break };
                let mut decoder = Decoder::new(1 << 20);
                let mut buf = [0u8; 4096];
                loop {
                    match stream.read(&mut buf) {
                        Ok(0) => break,
                        Ok(n) => {
                            if let Ok(msgs) = decoder.push(&buf[..n]) {
                                if let Some(msg) = msgs.into_iter().next() {
                                    let ack = parse_message(&msg, 1 << 20)
                                        .ok()
                                        .and_then(|m| {
                                            build_ack(&m, ack_code, "MOCKACK", "20260718100005")
                                        })
                                        .unwrap_or_else(|| {
                                            b"MSH|^~\\&|||||||ACK|1|P|2.5.1\rMSA|AE|\r".to_vec()
                                        });
                                    let _ = stream.write_all(&frame(&ack));
                                    break;
                                }
                            }
                        }
                        Err(_) => break,
                    }
                }
            }
        });
        Ok(Self {
            addr,
            handle: Some(handle),
        })
    }

    /// The address the mock LIS is listening on.
    #[must_use]
    pub fn addr(&self) -> SocketAddr {
        self.addr
    }
}

impl Drop for MockLis {
    fn drop(&mut self) {
        // Detach the accept thread; the process/test ending reclaims it.
        if let Some(handle) = self.handle.take() {
            drop(handle);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn delivers_and_receives_accept_ack() {
        let lis = MockLis::spawn(AckCode::Accept).unwrap();
        let oru = b"MSH|^~\\&|GATEWAY|LAB|LIS|HOSP|20260718100000||ORU^R01|C1|P|2.5.1\rOBX|1|NM|^^^GLU||5.30|mmol/L|||||F\r";
        let ack = send_message(lis.addr(), oru, Duration::from_secs(5), 1 << 20).unwrap();
        assert!(ack.is_accept(), "expected AA, got {:?}", ack.code);
    }

    #[test]
    fn reject_ack_is_not_accept() {
        let lis = MockLis::spawn(AckCode::Reject).unwrap();
        let oru = b"MSH|^~\\&|GATEWAY|LAB|LIS|HOSP|20260718100000||ORU^R01|C2|P|2.5.1\rOBX|1|NM|^^^GLU||1.0||||||F\r";
        let ack = send_message(lis.addr(), oru, Duration::from_secs(5), 1 << 20).unwrap();
        assert_eq!(ack.code, "AR");
        assert!(!ack.is_accept());
    }
}
