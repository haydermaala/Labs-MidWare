//! gatewayd — edge gateway daemon entrypoint.
//!
//! Default: prints a health snapshot (passive by default; opens no ports and
//! contacts no analyzer). `--demo` runs a synthetic ASTM session through the
//! ingestion pipeline against a temporary store and prints the outcome — no real
//! device, synthetic data only. Service/daemon packaging arrives in a later phase.
#![forbid(unsafe_code)]

mod pipeline;

use pipeline::{process_session, PipelineConfig};

fn main() {
    match std::env::args().nth(1).as_deref() {
        Some("--demo") => run_demo(),
        Some("--serve") => run_serve(),
        _ => print_health(),
    }
}

/// Serve the local API over loopback. Token comes from `GATEWAYD_API_TOKEN`
/// (never hard-coded). Seeds a synthetic session so the endpoints have data.
/// Blocking; manual use only.
fn run_serve() {
    use durable_queue::Store;
    use gateway_api::{serve, ApiConfig};
    use protocol_astm::encode_session;
    use std::sync::{Arc, Mutex};

    let token = match std::env::var("GATEWAYD_API_TOKEN") {
        Ok(t) if !t.is_empty() => t,
        _ => {
            eprintln!("set GATEWAYD_API_TOKEN to a bearer token to serve the local API");
            std::process::exit(2);
        }
    };
    let addr = std::env::args()
        .nth(2)
        .unwrap_or_else(|| "127.0.0.1:7373".to_owned());
    if !(addr.starts_with("127.") || addr.starts_with("localhost")) {
        eprintln!("refusing to bind non-loopback address {addr}");
        std::process::exit(2);
    }

    let dir = std::env::temp_dir().join(format!("gatewayd-serve-{}", std::process::id()));
    let _ = std::fs::create_dir_all(&dir);
    let mut store = Store::open(dir.join("edge.db")).expect("open store");

    let message: &[u8] =
        b"H|\\^&|||analyzer|||||host||P|1\rP|1||PID-SYNTH\rO|1|SPEC-1||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r";
    let session = encode_session(message, 4096);
    let _ = process_session(
        &mut store,
        &session,
        "sim:normal",
        &PipelineConfig::default(),
    )
    .expect("seed");

    println!("gatewayd serving on http://{addr} (loopback, bearer-authenticated)");
    println!("  GET /health   (no auth)   GET /status   GET /messages/recent");
    let shared = Arc::new(Mutex::new(store));
    serve(&addr, ApiConfig { token }, shared).expect("serve");
}

fn print_health() {
    let health = gateway_api::health();
    match serde_json::to_string_pretty(&health) {
        Ok(json) => println!("{json}"),
        Err(e) => {
            eprintln!("failed to render health: {e}");
            std::process::exit(1);
        }
    }
}

fn run_demo() {
    use durable_queue::Store;
    use protocol_astm::encode_session;

    // Synthetic message (never real patient data).
    let message: &[u8] =
        b"H|\\^&|||analyzer|||||host||P|1\rP|1||PID-SYNTH\rO|1|SPEC-1||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r";
    let session = encode_session(message, 24); // force multi-frame

    let dir = std::env::temp_dir().join(format!("gatewayd-demo-{}", std::process::id()));
    let _ = std::fs::create_dir_all(&dir);
    let mut store = Store::open(dir.join("edge.db")).expect("open store");
    let cfg = PipelineConfig::default();

    println!("gatewayd demo — synthetic ASTM session (passive/no device)");
    let outcomes = process_session(&mut store, &session, "sim:normal", &cfg).expect("process");
    for outcome in &outcomes {
        println!(
            "  message: {} result(s), raw={}, result_set={}, {}",
            outcome.results,
            outcome.raw_id,
            outcome.result_set_id,
            match &outcome.enqueue {
                durable_queue::Enqueue::Inserted(_) => "queued",
                durable_queue::Enqueue::Duplicate(_) => "duplicate suppressed",
            }
        );
    }

    // Re-process to demonstrate delivery deduplication.
    let _ = process_session(&mut store, &session, "sim:normal", &cfg).expect("process again");
    println!(
        "  after re-receiving the same session: pending deliveries = {} (dedup), audit events = {}",
        store.outbox_count("pending").unwrap_or(-1),
        store.audit_count().unwrap_or(-1),
    );

    let _ = std::fs::remove_dir_all(&dir);
}
