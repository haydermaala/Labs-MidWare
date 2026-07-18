//! gatewayd — edge gateway daemon entrypoint.
//!
//! Phase 1 scaffold: prints a health snapshot and exits. It intentionally opens
//! no ports and contacts no analyzer (safety boundary: passive by default,
//! no transmissions until a profile is validated). Service/daemon packaging and
//! the processing pipeline arrive in later phases.
#![forbid(unsafe_code)]

fn main() {
    let health = gateway_api::health();
    match serde_json::to_string_pretty(&health) {
        Ok(json) => println!("{json}"),
        Err(e) => {
            eprintln!("failed to render health: {e}");
            std::process::exit(1);
        }
    }
}
