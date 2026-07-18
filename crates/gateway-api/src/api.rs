//! The local gateway API: a pure request handler plus a thin `tiny_http` server.
//!
//! Routes (all JSON):
//! - `GET /health` — liveness (unauthenticated).
//! - `GET /status` — mode, schema version, outbox counts, audit count (auth).
//! - `GET /messages/recent?limit=N` — redaction-safe raw-message metadata,
//!   **no payloads** (auth).
//!
//! Every route except `/health` requires `Authorization: Bearer <token>`.

use std::sync::{Arc, Mutex};

use durable_queue::{RawMessageMeta, Store, StoreError};
use serde::Serialize;
use thiserror::Error;

use crate::{health, OperatingMode};

/// Configuration for the API (the bearer token it requires).
#[derive(Debug, Clone)]
pub struct ApiConfig {
    /// The token clients must present as `Authorization: Bearer <token>`.
    pub token: String,
}

/// A transport-independent API request (parsed from HTTP or constructed in tests).
#[derive(Debug, Clone)]
pub struct ApiRequest {
    /// HTTP method, e.g. "GET".
    pub method: String,
    /// Path without query string, e.g. "/status".
    pub path: String,
    /// Query parameters.
    pub query: Vec<(String, String)>,
    /// The bearer token presented, if any.
    pub bearer: Option<String>,
}

impl ApiRequest {
    /// A GET request helper (used in tests).
    #[must_use]
    pub fn get(path: &str, bearer: Option<&str>) -> Self {
        Self {
            method: "GET".to_owned(),
            path: path.to_owned(),
            query: Vec::new(),
            bearer: bearer.map(str::to_owned),
        }
    }

    fn query_get(&self, key: &str) -> Option<&str> {
        self.query
            .iter()
            .find(|(k, _)| k == key)
            .map(|(_, v)| v.as_str())
    }
}

/// A JSON API response.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ApiResponse {
    /// HTTP status code.
    pub status: u16,
    /// JSON body.
    pub body: String,
}

/// Errors from the server layer.
#[derive(Debug, Error)]
pub enum ApiError {
    /// Failed to bind/serve.
    #[error("server: {0}")]
    Server(String),
}

#[derive(Serialize)]
struct OutboxCounts {
    pending: i64,
    delivered: i64,
    dead: i64,
}

#[derive(Serialize)]
struct Status {
    service: &'static str,
    version: &'static str,
    mode: OperatingMode,
    schema_version: u32,
    outbox: OutboxCounts,
    audit_events: i64,
}

#[derive(Serialize)]
struct MessageMeta {
    id: String,
    transport: String,
    received_at: String,
    byte_len: i64,
}

impl From<RawMessageMeta> for MessageMeta {
    fn from(m: RawMessageMeta) -> Self {
        Self {
            id: m.id.to_string(),
            transport: m.transport,
            received_at: m.received_at,
            byte_len: m.byte_len,
        }
    }
}

fn json<T: Serialize>(status: u16, value: &T) -> ApiResponse {
    ApiResponse {
        status,
        body: serde_json::to_string(value).unwrap_or_else(|_| "{}".to_owned()),
    }
}

fn error(status: u16, message: &str) -> ApiResponse {
    ApiResponse {
        status,
        body: format!("{{\"error\":\"{message}\"}}"),
    }
}

fn build_status(store: &Store) -> Result<Status, StoreError> {
    Ok(Status {
        service: "gatewayd",
        version: crate::CRATE_VERSION,
        mode: OperatingMode::default(),
        schema_version: store.schema_version()?,
        outbox: OutboxCounts {
            pending: store.outbox_count("pending")?,
            delivered: store.outbox_count("delivered")?,
            dead: store.outbox_count("dead")?,
        },
        audit_events: store.audit_count()?,
    })
}

/// Handle a request against the store. Pure: no I/O beyond the store reads.
#[must_use]
pub fn handle(request: &ApiRequest, store: &Store, config: &ApiConfig) -> ApiResponse {
    // Liveness is unauthenticated.
    if request.method == "GET" && request.path == "/health" {
        return json(200, &health());
    }

    // Everything else requires a valid bearer token.
    if request.bearer.as_deref() != Some(config.token.as_str()) {
        return error(401, "unauthorized");
    }

    match (request.method.as_str(), request.path.as_str()) {
        ("GET", "/status") => match build_status(store) {
            Ok(status) => json(200, &status),
            Err(e) => error(500, &e.to_string()),
        },
        ("GET", "/messages/recent") => {
            let limit = request
                .query_get("limit")
                .and_then(|v| v.parse::<usize>().ok())
                .unwrap_or(20)
                .clamp(1, 100);
            match store.recent_raw_message_meta(limit) {
                Ok(rows) => {
                    let metas: Vec<MessageMeta> = rows.into_iter().map(Into::into).collect();
                    json(200, &metas)
                }
                Err(e) => error(500, &e.to_string()),
            }
        }
        _ => error(404, "not found"),
    }
}

/// Serve the API on `addr` (must be a loopback address) until the process exits.
/// Blocking; run on a dedicated thread. The store is shared behind a mutex.
pub fn serve(addr: &str, config: ApiConfig, store: Arc<Mutex<Store>>) -> Result<(), ApiError> {
    let server = tiny_http::Server::http(addr).map_err(|e| ApiError::Server(e.to_string()))?;
    for request in server.incoming_requests() {
        let api_request = to_api_request(&request);
        let response = {
            let store = store
                .lock()
                .map_err(|_| ApiError::Server("poisoned".into()))?;
            handle(&api_request, &store, &config)
        };
        let http = tiny_http::Response::from_string(response.body)
            .with_status_code(response.status)
            .with_header(
                tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..])
                    .expect("valid header"),
            );
        let _ = request.respond(http);
    }
    Ok(())
}

fn to_api_request(request: &tiny_http::Request) -> ApiRequest {
    let method = if *request.method() == tiny_http::Method::Get {
        "GET"
    } else {
        "OTHER"
    }
    .to_owned();

    let url = request.url();
    let (path, qs) = url.split_once('?').unwrap_or((url, ""));
    let query = qs
        .split('&')
        .filter(|s| !s.is_empty())
        .filter_map(|kv| {
            kv.split_once('=')
                .map(|(k, v)| (k.to_owned(), v.to_owned()))
        })
        .collect();

    let bearer = request
        .headers()
        .iter()
        .find(|h| h.field.equiv("Authorization"))
        .and_then(|h| h.value.as_str().strip_prefix("Bearer ").map(str::to_owned));

    ApiRequest {
        method,
        path: path.to_owned(),
        query,
        bearer,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn store_with_one_message() -> Store {
        let mut store = Store::open_in_memory().unwrap();
        let raw = store
            .insert_raw_message("astm", b"H|\\^&|\rL|1\r", Some("sim:normal"), None)
            .unwrap();
        let rs = store.insert_result_set(raw, b"{}").unwrap();
        store.enqueue("k1", Some(rs), b"{}").unwrap();
        store
    }

    fn config() -> ApiConfig {
        ApiConfig {
            token: "secret-token".to_owned(),
        }
    }

    #[test]
    fn health_is_unauthenticated() {
        let store = Store::open_in_memory().unwrap();
        let resp = handle(&ApiRequest::get("/health", None), &store, &config());
        assert_eq!(resp.status, 200);
        assert!(resp.body.contains("passive_capture"));
    }

    #[test]
    fn missing_or_wrong_token_is_unauthorized() {
        let store = Store::open_in_memory().unwrap();
        assert_eq!(
            handle(&ApiRequest::get("/status", None), &store, &config()).status,
            401
        );
        assert_eq!(
            handle(
                &ApiRequest::get("/status", Some("wrong")),
                &store,
                &config()
            )
            .status,
            401
        );
    }

    #[test]
    fn status_reports_counts() {
        let store = store_with_one_message();
        let resp = handle(
            &ApiRequest::get("/status", Some("secret-token")),
            &store,
            &config(),
        );
        assert_eq!(resp.status, 200);
        assert!(resp.body.contains("\"pending\":1"));
        assert!(resp.body.contains("passive_capture"));
        assert!(resp.body.contains("\"schema_version\":1"));
    }

    #[test]
    fn recent_messages_returns_metadata_without_payload() {
        let store = store_with_one_message();
        let resp = handle(
            &ApiRequest::get("/messages/recent", Some("secret-token")),
            &store,
            &config(),
        );
        assert_eq!(resp.status, 200);
        assert!(resp.body.contains("\"transport\":\"astm\""));
        assert!(resp.body.contains("byte_len"));
        // The raw payload bytes must NOT be present (redaction by default).
        assert!(!resp.body.contains("H|"));
        assert!(!resp.body.contains("payload"));
    }

    #[test]
    fn unknown_route_is_404() {
        let store = Store::open_in_memory().unwrap();
        let resp = handle(
            &ApiRequest::get("/nope", Some("secret-token")),
            &store,
            &config(),
        );
        assert_eq!(resp.status, 404);
    }
}
