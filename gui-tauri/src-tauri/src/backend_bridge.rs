use crate::vision_runtime::VisionObservation;
use crate::BackendRuntimeConfig;
use serde::{Deserialize, Serialize};
use std::time::Duration;

const REQUEST_TIMEOUT: Duration = Duration::from_secs(10);
// First use may spend up to 45 s starting OmniVoice and another 30 s warming it.
const PREFLIGHT_TIMEOUT: Duration = Duration::from_secs(90);

#[derive(Clone, Debug, Serialize)]
pub struct ReactionRequestBody {
    pub observation_id: String,
    pub event_age_ms: u64,
    pub observation: VisionObservation,
}

#[derive(Clone, Debug, Deserialize)]
pub struct Preflight {
    pub voice_label: String,
    pub voice_speed: f32,
}

#[derive(Clone, Debug, Deserialize)]
pub struct VisionConfig {
    pub engine: String,
    #[serde(default)]
    pub api_key: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ReactionPayload {
    pub observation_id: String,
    pub text: String,
    pub voice_label: String,
    pub voice_speed: f32,
    pub cloud_ms: u64,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ReactionReply {
    pub status: String,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub reaction: Option<ReactionPayload>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BridgeError {
    Transport,
    Unauthorized,
    InvalidResponse,
    Provider,
}

pub trait ReactionClient: Send + Sync {
    fn preflight(&self) -> Result<Preflight, BridgeError>;
    fn react(&self, request: &ReactionRequestBody) -> Result<ReactionReply, BridgeError>;
    fn cancel(&self, observation_id: &str) -> Result<(), BridgeError>;
    fn ack(&self, observation_id: &str, played: bool) -> Result<(), BridgeError>;
}

#[derive(Clone, Debug)]
pub struct BackendBridge {
    base_url: String,
    token: String,
    client: reqwest::blocking::Client,
    preflight_timeout: Duration,
}

impl BackendBridge {
    pub(crate) fn new(runtime: BackendRuntimeConfig) -> Result<Self, BridgeError> {
        Self::new_with_timeouts(runtime, REQUEST_TIMEOUT, PREFLIGHT_TIMEOUT)
    }

    fn new_with_timeouts(
        runtime: BackendRuntimeConfig,
        request_timeout: Duration,
        preflight_timeout: Duration,
    ) -> Result<Self, BridgeError> {
        let parsed =
            reqwest::Url::parse(&runtime.base_url).map_err(|_| BridgeError::InvalidResponse)?;
        if parsed.scheme() != "http"
            || parsed.host_str() != Some("127.0.0.1")
            || parsed.port().is_none()
            || parsed.username() != ""
            || parsed.password().is_some()
            || parsed.query().is_some()
            || parsed.fragment().is_some()
            || !matches!(parsed.path(), "" | "/")
        {
            return Err(BridgeError::InvalidResponse);
        }
        let client = reqwest::blocking::Client::builder()
            .timeout(request_timeout)
            .build()
            .map_err(|_| BridgeError::Transport)?;
        Ok(Self {
            base_url: runtime.base_url.trim_end_matches('/').to_owned(),
            token: runtime.token,
            client,
            preflight_timeout,
        })
    }

    fn post(&self, path: &str) -> reqwest::blocking::RequestBuilder {
        self.client
            .post(format!("{}{path}", self.base_url))
            .bearer_auth(&self.token)
    }

    fn map_status(status: reqwest::StatusCode) -> Result<(), BridgeError> {
        if status == reqwest::StatusCode::UNAUTHORIZED {
            return Err(BridgeError::Unauthorized);
        }
        if !status.is_success() {
            return Err(BridgeError::Provider);
        }
        Ok(())
    }

    fn get(&self, path: &str) -> reqwest::blocking::RequestBuilder {
        self.client
            .get(format!("{}{path}", self.base_url))
            .bearer_auth(&self.token)
    }

    pub fn vision_config(&self) -> Result<VisionConfig, BridgeError> {
        let response = self
            .get("/observation/vision-config")
            .send()
            .map_err(|_| BridgeError::Transport)?;
        Self::map_status(response.status())?;
        let reply = response
            .json::<VisionConfig>()
            .map_err(|_| BridgeError::InvalidResponse)?;
        if reply.engine != "gemini" && reply.engine != "local" {
            return Err(BridgeError::InvalidResponse);
        }
        Ok(reply)
    }
}

#[derive(Deserialize)]
struct PreflightReply {
    status: String,
    #[serde(default)]
    voice_label: String,
    #[serde(default)]
    voice_speed: f32,
}

#[derive(Deserialize)]
struct StatusReply {
    status: String,
}

impl ReactionClient for BackendBridge {
    fn preflight(&self) -> Result<Preflight, BridgeError> {
        let response = self
            .post("/observation/preflight")
            .timeout(self.preflight_timeout)
            .send()
            .map_err(|_| BridgeError::Transport)?;
        Self::map_status(response.status())?;
        let reply = response
            .json::<PreflightReply>()
            .map_err(|_| BridgeError::InvalidResponse)?;
        if reply.status != "ok"
            || reply.voice_label.trim().is_empty()
            || !reply.voice_speed.is_finite()
            || reply.voice_speed <= 0.0
        {
            return Err(BridgeError::Provider);
        }
        Ok(Preflight {
            voice_label: reply.voice_label,
            voice_speed: reply.voice_speed,
        })
    }

    fn react(&self, request: &ReactionRequestBody) -> Result<ReactionReply, BridgeError> {
        let response = self
            .post("/observation/react")
            .json(request)
            .send()
            .map_err(|_| BridgeError::Transport)?;
        Self::map_status(response.status())?;
        let reply = response
            .json::<ReactionReply>()
            .map_err(|_| BridgeError::InvalidResponse)?;
        match reply.status.as_str() {
            "ok" if reply.reaction.is_some() => Ok(reply),
            "skipped" if reply.reason.is_some() => Ok(reply),
            "error" => Err(BridgeError::Provider),
            _ => Err(BridgeError::InvalidResponse),
        }
    }

    fn cancel(&self, observation_id: &str) -> Result<(), BridgeError> {
        let response = self
            .post("/observation/cancel")
            .json(&serde_json::json!({"observation_id": observation_id}))
            .send()
            .map_err(|_| BridgeError::Transport)?;
        Self::map_status(response.status())?;
        let reply = response
            .json::<StatusReply>()
            .map_err(|_| BridgeError::InvalidResponse)?;
        if reply.status != "ok" {
            return Err(BridgeError::Provider);
        }
        Ok(())
    }

    fn ack(&self, observation_id: &str, played: bool) -> Result<(), BridgeError> {
        let response = self
            .post("/observation/ack")
            .json(&serde_json::json!({
                "observation_id": observation_id,
                "played": played,
            }))
            .send()
            .map_err(|_| BridgeError::Transport)?;
        Self::map_status(response.status())?;
        let reply = response
            .json::<StatusReply>()
            .map_err(|_| BridgeError::InvalidResponse)?;
        if reply.status != "ok" {
            return Err(BridgeError::Provider);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::thread;

    fn serve_once(response: &str) -> (String, thread::JoinHandle<Vec<u8>>) {
        serve_once_after(Duration::ZERO, response)
    }

    fn serve_once_after(delay: Duration, response: &str) -> (String, thread::JoinHandle<Vec<u8>>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let base_url = format!("http://{}", listener.local_addr().unwrap());
        let response = response.as_bytes().to_vec();
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = vec![0_u8; 16_384];
            let read = stream.read(&mut request).unwrap();
            request.truncate(read);
            thread::sleep(delay);
            write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                response.len()
            )
            .unwrap();
            stream.write_all(&response).unwrap();
            request
        });
        (base_url, handle)
    }

    fn request_body() -> ReactionRequestBody {
        ReactionRequestBody {
            observation_id: "obs-1".to_owned(),
            event_age_ms: 100,
            observation: VisionObservation {
                scene: "game".to_owned(),
                event: "miss".to_owned(),
                confidence: 0.9,
                novelty: 0.8,
                comment_worthiness: 0.7,
                visible_text: vec!["score".to_owned()],
            },
        }
    }

    #[test]
    fn reaction_body_contains_text_observation_but_never_frame_material() {
        let encoded = serde_json::to_string(&request_body()).unwrap();
        for forbidden in ["jpeg", "image", "base64", "data:", "fingerprint"] {
            assert!(!encoded.to_ascii_lowercase().contains(forbidden));
        }
        assert!(encoded.contains("comment_worthiness"));
    }

    #[test]
    fn bridge_posts_to_loopback_with_existing_bearer_token() {
        let (base_url, server) = serve_once(
            r#"{"status":"ok","reaction":{"observation_id":"obs-1","text":"Một câu.","voice_label":"Ru (OmniVoice)","voice_speed":1.0,"cloud_ms":100}}"#,
        );
        let bridge = BackendBridge::new(BackendRuntimeConfig {
            base_url,
            token: "secret-token".to_owned(),
        })
        .unwrap();

        let reply = bridge.react(&request_body()).unwrap();
        let request = String::from_utf8(server.join().unwrap()).unwrap();

        assert_eq!(reply.reaction.unwrap().text, "Một câu.");
        assert!(request.starts_with("POST /observation/react HTTP/1.1"));
        assert!(request
            .to_ascii_lowercase()
            .contains("authorization: bearer secret-token"));
        assert!(!request.contains("jpeg") && !request.contains("data:"));
    }

    #[test]
    fn bridge_rejects_every_non_loopback_base_url() {
        let runtime = BackendRuntimeConfig {
            base_url: "https://example.com".to_owned(),
            token: "secret".to_owned(),
        };
        assert_eq!(
            BackendBridge::new(runtime).unwrap_err(),
            BridgeError::InvalidResponse
        );
    }

    #[test]
    fn preflight_can_outlive_the_normal_request_timeout_while_omnivoice_warms() {
        let (base_url, server) = serve_once_after(
            Duration::from_millis(75),
            r#"{"status":"ok","voice_label":"Ru (OmniVoice)","voice_speed":1.0}"#,
        );
        let bridge = BackendBridge::new_with_timeouts(
            BackendRuntimeConfig {
                base_url,
                token: "secret-token".to_owned(),
            },
            Duration::from_millis(25),
            Duration::from_millis(200),
        )
        .unwrap();

        assert!(bridge.preflight().is_ok());
        server.join().unwrap();
    }

    #[test]
    fn ack_posts_played_flag_to_loopback_with_bearer_token() {
        let (base_url, server) = serve_once(r#"{"status":"ok"}"#);
        let bridge = BackendBridge::new(BackendRuntimeConfig {
            base_url,
            token: "secret-token".to_owned(),
        })
        .unwrap();

        assert!(bridge.ack("obs-7", true).is_ok());
        let request = String::from_utf8(server.join().unwrap()).unwrap();

        assert!(request.starts_with("POST /observation/ack HTTP/1.1"));
        assert!(request
            .to_ascii_lowercase()
            .contains("authorization: bearer secret-token"));
        assert!(request.contains("\"observation_id\":\"obs-7\""));
        assert!(request.contains("\"played\":true"));
    }

    #[test]
    fn ack_failure_maps_to_bridge_error() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let base_url = format!("http://{}", listener.local_addr().unwrap());
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = vec![0_u8; 16_384];
            let read = stream.read(&mut request).unwrap();
            request.truncate(read);
            write!(
                stream,
                "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}"
            )
            .unwrap();
            request
        });
        let bridge = BackendBridge::new(BackendRuntimeConfig {
            base_url,
            token: "secret".to_owned(),
        })
        .unwrap();

        assert_eq!(
            bridge.ack("obs-1", false).unwrap_err(),
            BridgeError::Unauthorized
        );
        handle.join().unwrap();
    }

    #[test]
    fn vision_config_returns_engine_and_key_from_loopback() {
        let (base_url, server) =
            serve_once(r#"{"status":"ok","engine":"gemini","api_key":"gem-key"}"#);
        let bridge = BackendBridge::new(BackendRuntimeConfig {
            base_url,
            token: "secret-token".to_owned(),
        })
        .unwrap();

        let config = bridge.vision_config().unwrap();
        let request = String::from_utf8(server.join().unwrap()).unwrap();

        assert_eq!(config.engine, "gemini");
        assert_eq!(config.api_key.as_deref(), Some("gem-key"));
        assert!(request.starts_with("GET /observation/vision-config HTTP/1.1"));
        assert!(request
            .to_ascii_lowercase()
            .contains("authorization: bearer secret-token"));
    }
}
