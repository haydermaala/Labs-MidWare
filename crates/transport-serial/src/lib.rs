//! Passive serial / USB virtual-COM capture.
//!
//! Provides strongly-typed serial line configuration and the passive capture
//! path (capture-only; ADR 0009 — there is no write-to-device API here). The
//! byte-level capture reuses `transport-core`'s bounded `capture_reader`, so the
//! capture logic is fully testable **without hardware** by feeding it any
//! `std::io::Read`.
//!
//! The concrete OS serial backend (opening a real port, enumerating ports) is
//! behind the optional `serialport-backend` feature. It is **off by default** so
//! ordinary builds and CI need no system libraries (e.g. `libudev` on Linux).
//! Real serial links are certified per model/firmware/OS with hardware in a later
//! phase; nothing here claims a device works without that validation.
#![forbid(unsafe_code)]

use std::io::Read;
use std::time::Duration;

use thiserror::Error;
use transport_core::{capture_reader, CaptureConfig, CaptureSink, TransportStats};

/// Semantic version of this crate, surfaced for provenance/audit.
pub const CRATE_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Number of data bits per character.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DataBits {
    /// 5 data bits.
    Five,
    /// 6 data bits.
    Six,
    /// 7 data bits.
    Seven,
    /// 8 data bits (default).
    #[default]
    Eight,
}

/// Parity checking mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Parity {
    /// No parity (default).
    #[default]
    None,
    /// Odd parity.
    Odd,
    /// Even parity.
    Even,
}

/// Number of stop bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StopBits {
    /// One stop bit (default).
    #[default]
    One,
    /// Two stop bits.
    Two,
}

/// Flow-control mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum FlowControl {
    /// No flow control (default).
    #[default]
    None,
    /// XON/XOFF software flow control.
    Software,
    /// RTS/CTS hardware flow control.
    Hardware,
}

/// Errors validating a serial configuration.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum SerialConfigError {
    /// The port name was empty.
    #[error("serial port name is empty")]
    EmptyPortName,
    /// The baud rate was zero.
    #[error("baud rate must be greater than zero")]
    ZeroBaud,
}

/// A serial line configuration for passive capture.
#[derive(Debug, Clone)]
pub struct SerialConfig {
    /// OS port name (e.g. "COM3" on Windows, "/dev/tty.usbserial-XXXX" on macOS).
    pub port_name: String,
    /// Baud rate (e.g. 9600, 19200, 115200).
    pub baud_rate: u32,
    /// Data bits per character.
    pub data_bits: DataBits,
    /// Parity mode.
    pub parity: Parity,
    /// Stop bits.
    pub stop_bits: StopBits,
    /// Flow control.
    pub flow_control: FlowControl,
    /// Read timeout; on timeout the capture ends so a reconnect can be scheduled.
    pub read_timeout: Duration,
    /// Byte-level capture bounds.
    pub capture: CaptureConfig,
}

impl SerialConfig {
    /// Construct a config with common defaults (8-N-1, no flow control).
    #[must_use]
    pub fn new(port_name: impl Into<String>, baud_rate: u32) -> Self {
        Self {
            port_name: port_name.into(),
            baud_rate,
            data_bits: DataBits::default(),
            parity: Parity::default(),
            stop_bits: StopBits::default(),
            flow_control: FlowControl::default(),
            read_timeout: Duration::from_secs(5),
            capture: CaptureConfig::default(),
        }
    }

    /// Validate the configuration.
    pub fn validate(&self) -> core::result::Result<(), SerialConfigError> {
        if self.port_name.trim().is_empty() {
            return Err(SerialConfigError::EmptyPortName);
        }
        if self.baud_rate == 0 {
            return Err(SerialConfigError::ZeroBaud);
        }
        Ok(())
    }

    /// A stable, non-PHI source label for captured chunks.
    #[must_use]
    pub fn source_label(&self) -> String {
        format!("serial:{}", self.port_name)
    }
}

/// Capture bytes from an already-open serial reader (or any `Read`) using the
/// bounded capture pipeline. This is the hardware-independent, testable core.
pub fn capture_serial<R: Read>(
    reader: R,
    config: &SerialConfig,
    sink: &CaptureSink,
    stats: &TransportStats,
) -> transport_core::Result<()> {
    stats.add_connection();
    let source = config.source_label();
    let result = capture_reader(reader, &source, &config.capture, sink, stats);
    stats.add_disconnect();
    result
}

/// Real OS serial backend (opening/enumerating ports). Enabled with the
/// `serialport-backend` feature; validated with hardware in a later phase.
#[cfg(feature = "serialport-backend")]
pub mod backend {
    use super::{DataBits, FlowControl, Parity, SerialConfig, StopBits};
    use transport_core::{CaptureError, CaptureSink, TransportStats};

    fn map_data_bits(d: DataBits) -> serialport::DataBits {
        match d {
            DataBits::Five => serialport::DataBits::Five,
            DataBits::Six => serialport::DataBits::Six,
            DataBits::Seven => serialport::DataBits::Seven,
            DataBits::Eight => serialport::DataBits::Eight,
        }
    }
    fn map_parity(p: Parity) -> serialport::Parity {
        match p {
            Parity::None => serialport::Parity::None,
            Parity::Odd => serialport::Parity::Odd,
            Parity::Even => serialport::Parity::Even,
        }
    }
    fn map_stop_bits(s: StopBits) -> serialport::StopBits {
        match s {
            StopBits::One => serialport::StopBits::One,
            StopBits::Two => serialport::StopBits::Two,
        }
    }
    fn map_flow(f: FlowControl) -> serialport::FlowControl {
        match f {
            FlowControl::None => serialport::FlowControl::None,
            FlowControl::Software => serialport::FlowControl::Software,
            FlowControl::Hardware => serialport::FlowControl::Hardware,
        }
    }

    /// Enumerate available serial ports by name.
    pub fn available_ports() -> transport_core::Result<Vec<String>> {
        serialport::available_ports()
            .map(|ports| ports.into_iter().map(|p| p.port_name).collect())
            .map_err(|e| CaptureError::Io(e.to_string()))
    }

    /// Open a real serial port for reading (passive capture).
    pub fn open(config: &SerialConfig) -> transport_core::Result<Box<dyn std::io::Read + Send>> {
        let port = serialport::new(&config.port_name, config.baud_rate)
            .data_bits(map_data_bits(config.data_bits))
            .parity(map_parity(config.parity))
            .stop_bits(map_stop_bits(config.stop_bits))
            .flow_control(map_flow(config.flow_control))
            .timeout(config.read_timeout)
            .open()
            .map_err(|e| CaptureError::Io(e.to_string()))?;
        Ok(Box::new(port))
    }

    /// Open the configured port and capture from it until it closes/errors.
    pub fn open_and_capture(
        config: &SerialConfig,
        sink: &CaptureSink,
        stats: &TransportStats,
    ) -> transport_core::Result<()> {
        config
            .validate()
            .map_err(|e| CaptureError::BoundExceeded(e.to_string()))?;
        let reader = open(config)?;
        super::capture_serial(reader, config, sink, stats)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn defaults_are_8n1_no_flow() {
        let cfg = SerialConfig::new("/dev/ttyUSB0", 9600);
        assert_eq!(cfg.data_bits, DataBits::Eight);
        assert_eq!(cfg.parity, Parity::None);
        assert_eq!(cfg.stop_bits, StopBits::One);
        assert_eq!(cfg.flow_control, FlowControl::None);
        assert!(cfg.validate().is_ok());
    }

    #[test]
    fn validation_rejects_empty_port_and_zero_baud() {
        assert_eq!(
            SerialConfig::new("", 9600).validate(),
            Err(SerialConfigError::EmptyPortName)
        );
        assert_eq!(
            SerialConfig::new("COM3", 0).validate(),
            Err(SerialConfigError::ZeroBaud)
        );
    }

    #[test]
    fn source_label_is_stable_and_labeled() {
        assert_eq!(
            SerialConfig::new("COM3", 9600).source_label(),
            "serial:COM3"
        );
    }

    #[test]
    fn captures_bytes_from_a_serial_like_reader() {
        // A Cursor stands in for an open serial port (hardware-independent).
        let (sink, rx) = CaptureSink::bounded(16);
        let stats = TransportStats::default();
        let cfg = SerialConfig::new("/dev/tty.usbserial-XYZ", 19200);
        capture_serial(Cursor::new(b"H|1|\\^&".to_vec()), &cfg, &sink, &stats).unwrap();
        drop(sink);

        let chunks: Vec<_> = rx.iter().collect();
        let all: Vec<u8> = chunks.iter().flat_map(|c| c.bytes.clone()).collect();
        assert_eq!(all, b"H|1|\\^&");
        assert_eq!(chunks[0].source, "serial:/dev/tty.usbserial-XYZ");
        assert_eq!(stats.snapshot().connections, 1);
        assert_eq!(stats.snapshot().disconnects, 1);
    }
}
