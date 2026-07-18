//! lab-simulator — synthetic ASTM scenario driver.
//!
//! Runs the ASTM fault-injection scenarios from `protocol-astm` and prints a
//! summary of the receiver's behavior for each. All data is synthetic by
//! construction; the simulator must never carry real patient data.
//!
//! Usage:
//!   lab-simulator            # run every scenario and print a summary
//!   lab-simulator <name>     # run one scenario by name
//!   lab-simulator --list     # list scenario names
#![forbid(unsafe_code)]

use protocol_astm::sim::Scenario;
use protocol_astm::{parse_message, run_scenario, LinkAction};

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
