//! gatewayd — edge gateway daemon entrypoint.
//!
//! Default: prints a health snapshot (passive by default; opens no ports and
//! contacts no analyzer). `--demo` runs a synthetic ASTM session through the
//! ingestion pipeline against a temporary store and prints the outcome — no real
//! device, synthetic data only. Service/daemon packaging arrives in a later phase.
#![forbid(unsafe_code)]

mod cloud;
mod daemon;
mod outbound;
mod pipeline;

use pipeline::{process_session, PipelineConfig};

fn main() {
    match std::env::args().nth(1).as_deref() {
        Some("--demo") => run_demo(),
        Some("--deliver-demo") => run_deliver_demo(),
        Some("--serve") => run_serve(),
        Some("--connect") => run_connect(),
        Some("--run") => run_daemon(),
        _ => print_health(),
    }
}

/// Run the continuous gateway daemon: enroll once, passively capture ASTM
/// sessions over TCP into the durable queue, and on each cycle report liveness +
/// PHI-free telemetry to the control plane. Configuration (in addition to the
/// `--connect` variables) :
///   GATEWAYD_CAPTURE_ADDR   loopback bind for the passive TCP capture listener
///                           (default 127.0.0.1:9600)
///   GATEWAYD_RUN_CYCLES     report cycles then exit; 0 = run until killed (default 0)
///   GATEWAYD_RUN_INTERVAL   seconds between report cycles (default 10)
/// Passive capture only — the gateway never writes to the analyzer, and no
/// message content leaves for the cloud.
fn run_daemon() {
    use cloud::{ensure_enrolled, ControlPlaneClient};
    use daemon::{cycle_interval, report_cycle, SessionAssembler};
    use durable_queue::Store;
    use std::net::SocketAddr;
    use std::sync::Arc;
    use std::time::Duration;
    use transport_core::{CaptureSink, TransportStats};
    use transport_tcp::{start, TcpCaptureConfig};

    let base = env_required("LC_CONTROL_PLANE_URL");
    let token = std::env::var("GATEWAYD_BOOTSTRAP_TOKEN").unwrap_or_default();
    let name = std::env::var("GATEWAYD_NAME").unwrap_or_else(|_| "edge-gateway".to_owned());
    let store_path = std::env::var("GATEWAYD_STORE").unwrap_or_else(|_| {
        std::env::temp_dir()
            .join("gatewayd-cloud.db")
            .to_string_lossy()
            .into_owned()
    });
    let capture_addr: SocketAddr = std::env::var("GATEWAYD_CAPTURE_ADDR")
        .unwrap_or_else(|_| "127.0.0.1:9600".to_owned())
        .parse()
        .unwrap_or_else(|_| {
            eprintln!("GATEWAYD_CAPTURE_ADDR must be a socket address");
            std::process::exit(2);
        });
    if !capture_addr.ip().is_loopback() {
        eprintln!("refusing to bind a non-loopback capture address {capture_addr}");
        std::process::exit(2);
    }
    let cycles: u64 = env_parse("GATEWAYD_RUN_CYCLES", 0);
    let interval = cycle_interval(env_parse("GATEWAYD_RUN_INTERVAL", 10));

    let mut store = Store::open(&store_path).expect("open edge store");
    let client = ControlPlaneClient::new(base, Duration::from_secs(20)).expect("tls setup");
    let enrollment = ensure_enrolled(&store, &client, &token, &name).unwrap_or_else(|e| {
        eprintln!("enrollment failed: {e}");
        std::process::exit(1);
    });
    println!(
        "enrolled gateway {} (tenant {})",
        enrollment.gateway_id, enrollment.tenant_id
    );

    // Passive TCP capture listener — the synthetic analyzer connects and streams.
    let (sink, rx) = CaptureSink::bounded(256);
    let stats = Arc::new(TransportStats::default());
    let server = start(TcpCaptureConfig::new(capture_addr), sink, stats).expect("start capture");
    println!(
        "capturing on tcp://{} — stream synthetic ASTM here (passive; device→gateway only)",
        server.local_addr()
    );

    let mut assembler = SessionAssembler::default();
    let mut cycle = 0u64;
    loop {
        std::thread::sleep(interval);
        let ingested = report_cycle(&rx, &mut assembler, &mut store, &client, &enrollment);
        cycle += 1;
        println!(
            "cycle {cycle}: ingested {ingested} message(s) this cycle; reported heartbeat + telemetry"
        );
        if cycles != 0 && cycle >= cycles {
            break;
        }
    }
    server.stop();
    println!("daemon stopped after {cycle} cycle(s)");
}

/// Parse an environment variable to `T`, falling back to `default` when unset or
/// unparseable.
fn env_parse<T: std::str::FromStr>(key: &str, default: T) -> T {
    std::env::var(key)
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(default)
}

/// Enroll against the LabConnect control plane, then report liveness and PHI-free
/// telemetry so the gateway appears `online` in the cloud fleet with its message
/// counts. Enrollment identity persists in the edge store, so re-runs reuse the
/// device credential (the bootstrap token is single-use). Configuration comes
/// from the environment:
///   LC_CONTROL_PLANE_URL      base URL (required, e.g. https://…up.railway.app)
///   GATEWAYD_BOOTSTRAP_TOKEN  single-use enrollment token (required first run)
///   GATEWAYD_STORE            edge db path (default: a temp file)
///   GATEWAYD_NAME             gateway display name (default: "edge-gateway")
///   GATEWAYD_SIM_SESSIONS     synthetic ASTM sessions to ingest first (default 0)
/// Synthetic only — this contacts no analyzer and reports no message content.
fn run_connect() {
    use cloud::{ensure_enrolled, ControlPlaneClient, Telemetry};
    use durable_queue::Store;
    use std::time::Duration;

    let base = env_required("LC_CONTROL_PLANE_URL");
    let token = std::env::var("GATEWAYD_BOOTSTRAP_TOKEN").unwrap_or_default();
    let name = std::env::var("GATEWAYD_NAME").unwrap_or_else(|_| "edge-gateway".to_owned());
    let store_path = std::env::var("GATEWAYD_STORE").unwrap_or_else(|_| {
        std::env::temp_dir()
            .join("gatewayd-cloud.db")
            .to_string_lossy()
            .into_owned()
    });
    let sim_sessions: u32 = std::env::var("GATEWAYD_SIM_SESSIONS")
        .ok()
        .and_then(|s| s.parse().ok())
        .unwrap_or(0);

    let mut store = Store::open(&store_path).expect("open edge store");
    let client = ControlPlaneClient::new(base, Duration::from_secs(20)).expect("tls setup");

    let enrollment = ensure_enrolled(&store, &client, &token, &name).unwrap_or_else(|e| {
        eprintln!("enrollment failed: {e}");
        std::process::exit(1);
    });
    println!(
        "enrolled gateway {} (tenant {})",
        enrollment.gateway_id, enrollment.tenant_id
    );

    // Optionally drive synthetic ASTM sessions through the ingestion pipeline into
    // the durable outbox, so telemetry reflects real captured/queued counts.
    if sim_sessions > 0 {
        let ingested = ingest_synthetic(&mut store, sim_sessions);
        println!("ingested {ingested} synthetic message(s) into the durable queue");
    }

    client
        .heartbeat(&enrollment.gateway_id, &enrollment.device_credential)
        .unwrap_or_else(|e| {
            eprintln!("heartbeat failed: {e}");
            std::process::exit(1);
        });
    println!("heartbeat ok — gateway is online in the cloud fleet");

    // Report PHI-free counts so the cloud fleet shows message throughput.
    let telemetry = Telemetry::from_store(&store).expect("snapshot telemetry");
    client
        .report_telemetry(
            &enrollment.gateway_id,
            &enrollment.device_credential,
            &telemetry,
        )
        .unwrap_or_else(|e| {
            eprintln!("telemetry report failed: {e}");
            std::process::exit(1);
        });
    println!(
        "telemetry reported: captured={} pending={} delivered={} dead={}",
        telemetry.captured, telemetry.pending, telemetry.delivered, telemetry.dead
    );

    // Pull any operator-published config (PHI-free; non-production settings only).
    match client.fetch_config(&enrollment.gateway_id, &enrollment.device_credential) {
        Ok(Some(cfg)) => println!(
            "config v{} ({}) published {}: {}",
            cfg.version, cfg.environment, cfg.published_at, cfg.settings_json
        ),
        Ok(None) => println!("no config published for this gateway yet"),
        Err(e) => eprintln!("config fetch failed (non-fatal): {e}"),
    }
}

/// Ingest `count` distinct synthetic ASTM sessions into the durable store,
/// returning how many messages were persisted. Each session varies its
/// specimen/patient ids so content-dedup does not collapse them. Synthetic data
/// only — no analyzer contact, no real patient information.
fn ingest_synthetic(store: &mut durable_queue::Store, count: u32) -> usize {
    use protocol_astm::encode_session;

    let mut total = 0usize;
    for i in 0..count {
        let message = format!(
            "H|\\^&|||analyzer|||||host||P|1\rP|1||PID-SYNTH-{i}\rO|1|SPEC-{i}||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r"
        );
        let session = encode_session(message.as_bytes(), 4096);
        match process_session(store, &session, "sim:normal", &PipelineConfig::default()) {
            Ok(outcomes) => total += outcomes.len(),
            Err(e) => eprintln!("synthetic ingest {i} failed: {e}"),
        }
    }
    total
}

/// Read a required environment variable or exit with a clear message.
fn env_required(key: &str) -> String {
    match std::env::var(key) {
        Ok(v) if !v.is_empty() => v,
        _ => {
            eprintln!("{key} must be set");
            std::process::exit(2);
        }
    }
}

/// End-to-end demo: ingest a synthetic ASTM session, then deliver the queued
/// result to an in-process mock LIS over HL7/MLLP and record the ACK. Synthetic
/// data only; no real device or LIS.
fn run_deliver_demo() {
    use durable_queue::Store;
    use outbound::deliver_pending;
    use protocol_astm::encode_session;
    use protocol_hl7::{AckCode, MockLis};
    use std::time::Duration;

    let message: &[u8] =
        b"H|\\^&|||analyzer|||||host||P|1\rP|1||PID-SYNTH\rO|1|SPEC-1||^^^GLU\rR|1|^^^GLU|5.30|mmol/L|3.9^5.6|N||F\rL|1|N\r";
    let session = encode_session(message, 24);

    let dir = std::env::temp_dir().join(format!("gatewayd-deliver-{}", std::process::id()));
    let _ = std::fs::create_dir_all(&dir);
    let mut store = Store::open(dir.join("edge.db")).expect("open store");

    let outcomes = process_session(
        &mut store,
        &session,
        "sim:normal",
        &PipelineConfig::default(),
    )
    .expect("ingest");
    println!(
        "ingested {} message(s); pending deliveries = {}",
        outcomes.len(),
        store.outbox_count("pending").unwrap_or(-1)
    );

    let lis = MockLis::spawn(AckCode::Accept).expect("mock LIS");
    let report =
        deliver_pending(&mut store, lis.addr(), Duration::from_secs(5), 3, 100).expect("deliver");
    println!(
        "delivered={} dead={} retried={}; outbox delivered={} pending={}",
        report.delivered,
        report.dead,
        report.retried,
        store.outbox_count("delivered").unwrap_or(-1),
        store.outbox_count("pending").unwrap_or(-1),
    );

    let _ = std::fs::remove_dir_all(&dir);
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
