//! Passive TCP capture — listener (server) and client.
//!
//! Some analyzers/workstations connect to us (we listen); others expect us to
//! connect to them (we act as client). Both are **capture-only**: bytes flow one
//! way, device → gateway. There is no send path (see `transport-core`'s
//! capture-only contract, ADR 0009).
//!
//! The listener bounds concurrency (`max_connections`), can restrict peers to an
//! allowlist, and per-connection reads are bounded by `transport-core`'s
//! `capture_reader`. TLS is a later addition (**OPEN**); Phase 3 captures plain
//! TCP on a trusted analyzer VLAN.
#![forbid(unsafe_code)]

use std::net::{IpAddr, SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

use transport_core::{
    capture_reader, CaptureConfig, CaptureError, CaptureSink, Result, TransportStats,
};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Configuration for a passive TCP capture listener.
#[derive(Debug, Clone)]
pub struct TcpCaptureConfig {
    /// Address to bind. Use port 0 for an OS-assigned ephemeral port.
    pub bind_addr: SocketAddr,
    /// Maximum concurrent connections. Excess connections are closed immediately.
    pub max_connections: usize,
    /// Allowed peer IPs. Empty means allow any (use only on a trusted VLAN).
    pub peer_allowlist: Vec<IpAddr>,
    /// Optional per-read idle timeout; on timeout the connection ends (reconnect
    /// is the caller's concern via a backoff policy).
    pub read_timeout: Option<Duration>,
    /// Byte-level capture bounds.
    pub capture: CaptureConfig,
}

impl TcpCaptureConfig {
    /// A sensible default config for `bind_addr` (allow-any, 16 connections).
    #[must_use]
    pub fn new(bind_addr: SocketAddr) -> Self {
        Self {
            bind_addr,
            max_connections: 16,
            peer_allowlist: Vec::new(),
            read_timeout: None,
            capture: CaptureConfig::default(),
        }
    }

    fn peer_allowed(&self, peer: &SocketAddr) -> bool {
        self.peer_allowlist.is_empty() || self.peer_allowlist.contains(&peer.ip())
    }
}

/// A handle to a running capture listener. Dropping it (or calling [`stop`]) stops
/// accepting new connections.
///
/// [`stop`]: RunningServer::stop
pub struct RunningServer {
    local_addr: SocketAddr,
    shutdown: Arc<AtomicBool>,
    accept_handle: Option<JoinHandle<()>>,
}

impl RunningServer {
    /// The actual bound address (useful when binding to port 0).
    #[must_use]
    pub fn local_addr(&self) -> SocketAddr {
        self.local_addr
    }

    /// Stop accepting new connections and join the accept loop.
    pub fn stop(mut self) {
        self.begin_shutdown();
    }

    fn begin_shutdown(&mut self) {
        self.shutdown.store(true, Ordering::SeqCst);
        if let Some(handle) = self.accept_handle.take() {
            let _ = handle.join();
        }
    }
}

impl Drop for RunningServer {
    fn drop(&mut self) {
        self.begin_shutdown();
    }
}

/// Start a passive TCP capture listener. Captured chunks are pushed to `sink`;
/// counters accumulate in `stats`. Returns immediately with a running handle.
pub fn start(
    config: TcpCaptureConfig,
    sink: CaptureSink,
    stats: Arc<TransportStats>,
) -> Result<RunningServer> {
    let listener =
        TcpListener::bind(config.bind_addr).map_err(|e| CaptureError::Io(e.to_string()))?;
    let local_addr = listener
        .local_addr()
        .map_err(|e| CaptureError::Io(e.to_string()))?;
    listener
        .set_nonblocking(true)
        .map_err(|e| CaptureError::Io(e.to_string()))?;

    let shutdown = Arc::new(AtomicBool::new(false));
    let active = Arc::new(AtomicUsize::new(0));
    let accept_shutdown = shutdown.clone();

    let handle = thread::spawn(move || {
        accept_loop(listener, config, sink, stats, accept_shutdown, active);
    });

    Ok(RunningServer {
        local_addr,
        shutdown,
        accept_handle: Some(handle),
    })
}

fn accept_loop(
    listener: TcpListener,
    config: TcpCaptureConfig,
    sink: CaptureSink,
    stats: Arc<TransportStats>,
    shutdown: Arc<AtomicBool>,
    active: Arc<AtomicUsize>,
) {
    while !shutdown.load(Ordering::Acquire) {
        match listener.accept() {
            Ok((stream, peer)) => {
                if !config.peer_allowed(&peer)
                    || active.load(Ordering::Acquire) >= config.max_connections
                {
                    // Reject: close immediately. Not counted as an accepted connection.
                    drop(stream);
                    continue;
                }
                active.fetch_add(1, Ordering::AcqRel);
                stats.add_connection();

                let sink = sink.clone();
                let stats = stats.clone();
                let active = active.clone();
                let capture = config.capture;
                let read_timeout = config.read_timeout;

                thread::spawn(move || {
                    if let Some(timeout) = read_timeout {
                        let _ = stream.set_read_timeout(Some(timeout));
                    }
                    let source = format!("tcp:{peer}");
                    let _ = capture_reader(stream, &source, &capture, &sink, &stats);
                    stats.add_disconnect();
                    active.fetch_sub(1, Ordering::AcqRel);
                });
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                thread::sleep(Duration::from_millis(5));
            }
            Err(_) => {
                thread::sleep(Duration::from_millis(5));
            }
        }
    }
}

/// Connect to a remote analyzer as a client and capture inbound bytes until the
/// connection closes. Reconnection is the caller's concern (apply a
/// `transport_core::BackoffPolicy`).
pub fn connect_and_capture(
    addr: SocketAddr,
    config: &CaptureConfig,
    read_timeout: Option<Duration>,
    sink: &CaptureSink,
    stats: &TransportStats,
) -> Result<()> {
    let stream = TcpStream::connect(addr).map_err(|e| CaptureError::Io(e.to_string()))?;
    if let Some(timeout) = read_timeout {
        let _ = stream.set_read_timeout(Some(timeout));
    }
    stats.add_connection();
    let source = format!("tcp-client:{addr}");
    let result = capture_reader(stream, &source, config, sink, stats);
    stats.add_disconnect();
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::net::{Ipv4Addr, SocketAddr};

    fn loopback(port: u16) -> SocketAddr {
        SocketAddr::from((Ipv4Addr::LOCALHOST, port))
    }

    #[test]
    fn crate_version_present() {
        assert!(!CRATE_VERSION.is_empty());
    }

    #[test]
    fn server_captures_bytes_from_a_connection() {
        let (sink, rx) = CaptureSink::bounded(64);
        let stats = Arc::new(TransportStats::default());
        let server = start(TcpCaptureConfig::new(loopback(0)), sink, stats.clone()).unwrap();
        let addr = server.local_addr();

        let client = thread::spawn(move || {
            let mut s = TcpStream::connect(addr).unwrap();
            s.write_all(b"H|1|glucose").unwrap();
            // Closing flushes EOF so the server's capture loop ends.
        });
        client.join().unwrap();

        // Collect whatever arrived within a short window.
        let mut got = Vec::new();
        while let Ok(chunk) = rx.recv_timeout(Duration::from_secs(2)) {
            got.extend_from_slice(&chunk.bytes);
            if got == b"H|1|glucose" {
                break;
            }
        }
        assert_eq!(got, b"H|1|glucose");
        assert!(stats.snapshot().connections >= 1);
        server.stop();
    }

    #[test]
    fn allowlist_rejects_disallowed_peer() {
        let (sink, rx) = CaptureSink::bounded(64);
        let stats = Arc::new(TransportStats::default());
        let mut config = TcpCaptureConfig::new(loopback(0));
        // Only allow a non-loopback address; our test client is loopback → rejected.
        config.peer_allowlist = vec![IpAddr::V4(Ipv4Addr::new(10, 0, 0, 1))];
        let server = start(config, sink, stats.clone()).unwrap();
        let addr = server.local_addr();

        let _ = TcpStream::connect(addr).and_then(|mut s| s.write_all(b"nope"));

        // Nothing should be captured; connection is not accepted.
        assert!(rx.recv_timeout(Duration::from_millis(300)).is_err());
        assert_eq!(stats.snapshot().connections, 0);
        server.stop();
    }

    #[test]
    fn zero_max_connections_captures_nothing() {
        let (sink, rx) = CaptureSink::bounded(64);
        let stats = Arc::new(TransportStats::default());
        let mut config = TcpCaptureConfig::new(loopback(0));
        config.max_connections = 0;
        let server = start(config, sink, stats.clone()).unwrap();
        let addr = server.local_addr();

        let _ = TcpStream::connect(addr).and_then(|mut s| s.write_all(b"nope"));

        assert!(rx.recv_timeout(Duration::from_millis(300)).is_err());
        assert_eq!(stats.snapshot().connections, 0);
        server.stop();
    }

    #[test]
    fn client_captures_from_remote_analyzer() {
        // A stand-in "analyzer" that accepts one connection and sends bytes.
        let listener = TcpListener::bind(loopback(0)).unwrap();
        let addr = listener.local_addr().unwrap();
        let analyzer = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream.write_all(b"R|1|5.30|mmol/L").unwrap();
        });

        let (sink, rx) = CaptureSink::bounded(64);
        let stats = TransportStats::default();
        let cfg = CaptureConfig::default();
        connect_and_capture(addr, &cfg, None, &sink, &stats).unwrap();
        drop(sink);
        analyzer.join().unwrap();

        let all: Vec<u8> = rx.iter().flat_map(|c| c.bytes).collect();
        assert_eq!(all, b"R|1|5.30|mmol/L");
        assert_eq!(stats.snapshot().connections, 1);
        assert_eq!(stats.snapshot().disconnects, 1);
    }
}
