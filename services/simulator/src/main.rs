//! lab-simulator — synthetic analyzer scenario driver.
//!
//! Phase 1 scaffold: prints available (stub) scenarios. Real ASTM link-layer,
//! HL7 MLLP, and fault-injection scenarios are added in Phase 4. All data is
//! synthetic by construction; the simulator must never carry real patient data.
#![forbid(unsafe_code)]

fn main() {
    println!(
        "lab-simulator {} (synthetic data only)",
        env!("CARGO_PKG_VERSION")
    );
    println!("scenarios: [] (populated in Phase 4)");
    // Reference the protocol crate so the dependency graph is exercised.
    let _ = protocol_astm::crate_name();
}
