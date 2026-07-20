//! Control-plane client — the edge gateway's outbound link to the LabConnect
//! cloud. Three calls: enrollment (redeem a single-use bootstrap token for a
//! device credential), heartbeat (liveness), and config fetch.
//!
//! Direction is strictly edge → cloud over HTTPS (the control plane is not a
//! device driver and never reaches back into the analyzer). PHI never crosses
//! this boundary: enrollment carries only a gateway name; heartbeat and config
//! carry no message content. The device credential is shown once by the control
//! plane at enrollment and then persisted in the edge store, because the
//! bootstrap token is single-use and cannot be redeemed again.

use std::sync::Arc;
use std::time::Duration;

use durable_queue::Store;
use serde::Deserialize;

/// Edge-store config keys under which enrollment identity is persisted.
const KEY_GATEWAY_ID: &str = "cloud.gateway_id";
const KEY_DEVICE_CREDENTIAL: &str = "cloud.device_credential";
const KEY_TENANT_ID: &str = "cloud.tenant_id";

#[derive(Debug, thiserror::Error)]
pub enum CloudError {
    #[error("control-plane request failed: {0}")]
    Http(String),
    #[error("unexpected status {status} from {path}")]
    Status { status: u16, path: String },
    #[error("invalid response body: {0}")]
    Body(String),
    #[error("edge store error: {0}")]
    Store(String),
    #[error("tls setup failed: {0}")]
    Tls(String),
}

/// The identity a gateway receives at enrollment. `device_credential` is the
/// long-lived secret used to authenticate subsequent heartbeat/config calls.
#[derive(Debug, Clone, Deserialize)]
pub struct Enrollment {
    #[serde(rename = "gatewayId")]
    pub gateway_id: String,
    #[serde(rename = "tenantId")]
    pub tenant_id: String,
    #[serde(rename = "deviceCredential")]
    pub device_credential: String,
}

/// Published config for this gateway (PHI-free operational settings). Unknown
/// wire fields (e.g. the echoed gatewayId) are ignored by serde.
#[derive(Debug, Clone, Deserialize)]
pub struct ConfigView {
    pub version: i64,
    pub environment: String,
    #[serde(rename = "settingsJson")]
    pub settings_json: String,
    #[serde(rename = "publishedAt")]
    pub published_at: String,
}

/// A blocking HTTPS client bound to one control-plane base URL.
pub struct ControlPlaneClient {
    base_url: String,
    agent: ureq::Agent,
}

impl ControlPlaneClient {
    /// Build a client for a control-plane base URL (e.g.
    /// "https://labs-midware-staging.up.railway.app"). TLS uses the OS trust
    /// store; `timeout` bounds each call.
    pub fn new(base_url: impl Into<String>, timeout: Duration) -> Result<Self, CloudError> {
        let connector =
            native_tls::TlsConnector::new().map_err(|e| CloudError::Tls(e.to_string()))?;
        let agent = ureq::AgentBuilder::new()
            .timeout(timeout)
            .tls_connector(Arc::new(connector))
            .build();
        Ok(Self {
            base_url: base_url.into().trim_end_matches('/').to_owned(),
            agent,
        })
    }

    fn url(&self, path: &str) -> String {
        format!("{}{}", self.base_url, path)
    }

    /// Redeem a single-use bootstrap token, returning the gateway identity and
    /// device credential. The token is consumed server-side on success.
    pub fn enroll(&self, bootstrap_token: &str, name: &str) -> Result<Enrollment, CloudError> {
        let path = "/api/gateways/enroll";
        let body = serde_json::json!({ "bootstrapToken": bootstrap_token, "name": name });
        let resp = self
            .agent
            .post(&self.url(path))
            .set("Content-Type", "application/json")
            .send_string(&body.to_string());
        let text = read_body(resp, path)?;
        serde_json::from_str::<Enrollment>(&text).map_err(|e| CloudError::Body(e.to_string()))
    }

    /// Report liveness (204 on success). This alone makes the gateway show
    /// `online` in the cloud fleet view.
    pub fn heartbeat(&self, gateway_id: &str, credential: &str) -> Result<(), CloudError> {
        let path = "/api/gateways/heartbeat";
        let resp = self
            .agent
            .post(&self.url(path))
            .set("X-Gateway-Id", gateway_id)
            .set("X-Gateway-Credential", credential)
            .call();
        read_body(resp, path).map(|_| ())
    }

    /// Fetch this gateway's published config, or None if none is published (204).
    pub fn fetch_config(
        &self,
        gateway_id: &str,
        credential: &str,
    ) -> Result<Option<ConfigView>, CloudError> {
        let path = "/api/gateways/config";
        let resp = self
            .agent
            .get(&self.url(path))
            .set("X-Gateway-Id", gateway_id)
            .set("X-Gateway-Credential", credential)
            .call();
        match resp {
            Ok(r) if r.status() == 204 => Ok(None),
            Ok(r) => {
                let text = r
                    .into_string()
                    .map_err(|e| CloudError::Http(e.to_string()))?;
                serde_json::from_str::<ConfigView>(&text)
                    .map(Some)
                    .map_err(|e| CloudError::Body(e.to_string()))
            }
            Err(ureq::Error::Status(code, _)) => Err(CloudError::Status {
                status: code,
                path: path.to_owned(),
            }),
            Err(e) => Err(CloudError::Http(e.to_string())),
        }
    }
}

/// Interpret a ureq result: 2xx → body text; a non-2xx status or transport error
/// becomes a typed `CloudError` (ureq surfaces non-2xx as `Error::Status`).
fn read_body(resp: Result<ureq::Response, ureq::Error>, path: &str) -> Result<String, CloudError> {
    match resp {
        Ok(r) => r.into_string().map_err(|e| CloudError::Http(e.to_string())),
        Err(ureq::Error::Status(code, _)) => Err(CloudError::Status {
            status: code,
            path: path.to_owned(),
        }),
        Err(e) => Err(CloudError::Http(e.to_string())),
    }
}

/// Return the stored enrollment, enrolling first if none is persisted. The
/// device credential is written durably so a restart reuses it rather than
/// attempting to redeem the (single-use) bootstrap token again.
pub fn ensure_enrolled(
    store: &Store,
    client: &ControlPlaneClient,
    bootstrap_token: &str,
    name: &str,
) -> Result<Enrollment, CloudError> {
    let stored_id = store.get_config(KEY_GATEWAY_ID).map_err(store_err)?;
    let stored_cred = store.get_config(KEY_DEVICE_CREDENTIAL).map_err(store_err)?;
    if let (Some(gateway_id), Some(device_credential)) = (stored_id, stored_cred) {
        let tenant_id = store
            .get_config(KEY_TENANT_ID)
            .map_err(store_err)?
            .unwrap_or_default();
        return Ok(Enrollment {
            gateway_id,
            tenant_id,
            device_credential,
        });
    }

    let enrollment = client.enroll(bootstrap_token, name)?;
    store
        .set_config(KEY_GATEWAY_ID, &enrollment.gateway_id)
        .map_err(store_err)?;
    store
        .set_config(KEY_DEVICE_CREDENTIAL, &enrollment.device_credential)
        .map_err(store_err)?;
    store
        .set_config(KEY_TENANT_ID, &enrollment.tenant_id)
        .map_err(store_err)?;
    Ok(enrollment)
}

fn store_err<E: std::fmt::Display>(e: E) -> CloudError {
    CloudError::Store(e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;

    /// One-shot loopback HTTP server: accepts a single request and replies with a
    /// canned response. Returns the base URL to point a client at (plain http —
    /// TLS is exercised only against real https endpoints).
    fn serve_once(status_line: &'static str, body: String) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        std::thread::spawn(move || {
            if let Ok((mut stream, _)) = listener.accept() {
                let mut buf = [0u8; 4096];
                let _ = stream.read(&mut buf);
                let resp = format!(
                    "{status_line}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = stream.write_all(resp.as_bytes());
            }
        });
        format!("http://{addr}")
    }

    fn client(base: String) -> ControlPlaneClient {
        ControlPlaneClient::new(base, Duration::from_secs(5)).unwrap()
    }

    #[test]
    fn enroll_parses_the_camel_case_response() {
        let base = serve_once(
            "HTTP/1.1 200 OK",
            r#"{"gatewayId":"gw_1","tenantId":"ten_1","deviceCredential":"cred_abc"}"#.to_owned(),
        );
        let e = client(base).enroll("boot_tok", "edge-1").unwrap();
        assert_eq!(e.gateway_id, "gw_1");
        assert_eq!(e.tenant_id, "ten_1");
        assert_eq!(e.device_credential, "cred_abc");
    }

    #[test]
    fn heartbeat_accepts_204() {
        let base = serve_once("HTTP/1.1 204 No Content", String::new());
        assert!(client(base).heartbeat("gw_1", "cred_abc").is_ok());
    }

    #[test]
    fn heartbeat_surfaces_a_401_as_a_typed_status() {
        let base = serve_once("HTTP/1.1 401 Unauthorized", String::new());
        match client(base).heartbeat("gw_1", "bad") {
            Err(CloudError::Status { status: 401, .. }) => {}
            other => panic!("expected 401 status error, got {other:?}"),
        }
    }

    #[test]
    fn ensure_enrolled_persists_then_reuses_without_a_second_call() {
        let store = Store::open_in_memory().unwrap();
        let base = serve_once(
            "HTTP/1.1 200 OK",
            r#"{"gatewayId":"gw_9","tenantId":"ten_9","deviceCredential":"cred_9"}"#.to_owned(),
        );
        let first = ensure_enrolled(&store, &client(base), "tok", "edge").unwrap();
        assert_eq!(first.gateway_id, "gw_9");

        // A second call must NOT hit the network: point at a dead port and prove
        // it still resolves from the durable store.
        let dead =
            ControlPlaneClient::new("http://127.0.0.1:1", Duration::from_millis(250)).unwrap();
        let second = ensure_enrolled(&store, &dead, "tok", "edge").unwrap();
        assert_eq!(second.gateway_id, "gw_9");
        assert_eq!(second.device_credential, "cred_9");
        assert_eq!(second.tenant_id, "ten_9");
    }
}
