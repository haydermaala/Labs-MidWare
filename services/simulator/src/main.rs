//! lab-simulator — synthetic ASTM scenario driver.
//!
//! Runs the ASTM fault-injection scenarios from `protocol-astm` and prints a
//! summary of the receiver's behavior for each. All data is synthetic by
//! construction; the simulator must never carry real patient data.
//!
//! Usage:
//!   lab-simulator                     # run every scenario and print a summary
//!   lab-simulator <name>              # run one scenario by name
//!   lab-simulator --list              # list scenario names
//!   lab-simulator --emit-tcp <addr> [count]
//!                                     # stream <count> synthetic ASTM sessions to
//!                                     # a passive capture listener (e.g. gatewayd
//!                                     # --run). Default count = 3.
#![forbid(unsafe_code)]

use std::io::Write;
use std::net::TcpStream;

use protocol_astm::sim::Scenario;
use protocol_astm::{encode_session, parse_message, run_scenario, LinkAction};

fn main() {
    println!(
        "lab-simulator {} (synthetic data only)",
        env!("CARGO_PKG_VERSION")
    );

    let arg = std::env::args().nth(1);
    match arg.as_deref() {
        Some("--list") => {
            for s in Scenario::ALL {
                println!("  {}", s.name());
            }
        }
        Some("--emit-tcp") => {
            let addr = std::env::args().nth(2).unwrap_or_else(|| {
                eprintln!("usage: lab-simulator --emit-tcp <addr> [count]");
                std::process::exit(2);
            });
            let count: u32 = std::env::args()
                .nth(3)
                .and_then(|s| s.parse().ok())
                .unwrap_or(3);
            emit_tcp(&addr, count);
        }
        Some(name) => match Scenario::ALL.iter().find(|s| s.name() == name) {
            Some(&scenario) => report(scenario),
            None => {
                eprintln!("unknown scenario '{name}'. Try --list.");
                std::process::exit(2);
            }
        },
        None => {
            for &scenario in Scenario::ALL {
                report(scenario);
            }
        }
    }
}

/// Stream `count` distinct synthetic ASTM sessions to a passive capture listener,
/// each on its own short-lived TCP connection (mirroring how an analyzer connects
/// per result batch). All data is synthetic — no real patient information.
fn emit_tcp(addr: &str, count: u32) {
    for i in 0..count {
        let msg = format!(
            "H|\\^&|||analyzer|||||host||P|1\rP|1||PID-SYNTH-{i}\rO|1|SPEC-{i}||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r"
        );
        let session = encode_session(msg.as_bytes(), 4096);
        match TcpStream::connect(addr).and_then(|mut s| s.write_all(&session)) {
            Ok(()) => println!("emitted synthetic session {} to {addr}", i + 1),
            Err(e) => {
                eprintln!("failed to emit to {addr}: {e}");
                std::process::exit(1);
            }
        }
    }
    println!("done — {count} synthetic session(s) sent");
}

fn report(scenario: Scenario) {
    let run = run_scenario(scenario);
    let acks = count(&run.actions, LinkAction::SendAck);
    let naks = count(&run.actions, LinkAction::SendNak);
    let delivers = count(&run.actions, LinkAction::DeliverAndAck);

    let outcome = match &run.delivered {
        Some(msg) => match parse_message(msg, 64 * 1024) {
            Ok(parsed) => format!("delivered ({} records)", parsed.records.len()),
            Err(e) => format!("delivered but unparsable: {e}"),
        },
        None if run.timed_out => "aborted (timeout)".to_owned(),
        None => "no delivery".to_owned(),
    };

    println!(
        "{:<20} acks={acks} naks={naks} delivered_frames={delivers} -> {outcome}",
        scenario.name()
    );
}

fn count(actions: &[LinkAction], target: LinkAction) -> usize {
    actions.iter().filter(|a| **a == target).count()
}
