use crate::frame_buffer::PreparedFrame;
use base64::Engine;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::io;
use std::net::{Ipv4Addr, SocketAddrV4, TcpListener};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

const MODEL_BYTES: u64 = 1_107_409_952;
const MMPROJ_BYTES: u64 = 445_053_216;
const HEALTH_TIMEOUT: Duration = Duration::from_secs(45);
const HTTP_TIMEOUT: Duration = Duration::from_secs(10);
pub(crate) const VISION_PROMPT: &str = "Inspect these consecutive primary-monitor frames and return only JSON. Describe the current scene, the newest visually supported event, confidence, novelty, whether the event is worth a brief social comment, and any relevant visible text. Do not give advice, diagnose software, or invent activity outside the frames.";

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct VisionObservation {
    pub scene: String,
    pub event: String,
    pub confidence: f32,
    pub novelty: f32,
    pub comment_worthiness: f32,
    pub visible_text: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VisionError {
    MissingAsset,
    ServerStart,
    Http,
    InvalidSchema,
    Cancelled,
    Gemini(String),
}

#[derive(Clone, Debug)]
pub struct VisionServerConfig {
    pub executable: PathBuf,
    pub model: PathBuf,
    pub mmproj: PathBuf,
    pub port: u16,
}

impl VisionServerConfig {
    pub fn for_project(project_root: &Path, port: u16) -> Self {
        let model_dir = project_root.join("models/vision/qwen3-vl-2b-instruct");
        Self {
            executable: project_root.join("tools/llama.cpp-bin/llama-server.exe"),
            model: model_dir.join("Qwen3VL-2B-Instruct-Q4_K_M.gguf"),
            mmproj: model_dir.join("mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf"),
            port,
        }
    }

    pub fn args(&self) -> Vec<String> {
        vec![
            "--model".to_owned(),
            self.model.to_string_lossy().into_owned(),
            "--mmproj".to_owned(),
            self.mmproj.to_string_lossy().into_owned(),
            "--host".to_owned(),
            "127.0.0.1".to_owned(),
            "--port".to_owned(),
            self.port.to_string(),
            "--ctx-size".to_owned(),
            "2048".to_owned(),
            "--n-predict".to_owned(),
            "192".to_owned(),
            "--parallel".to_owned(),
            "1".to_owned(),
            "--n-gpu-layers".to_owned(),
            "99".to_owned(),
            "--flash-attn".to_owned(),
            "on".to_owned(),
            "--cache-type-k".to_owned(),
            "q8_0".to_owned(),
            "--cache-type-v".to_owned(),
            "q8_0".to_owned(),
            "--image-max-tokens".to_owned(),
            "96".to_owned(),
            "--reasoning".to_owned(),
            "off".to_owned(),
            "--no-webui".to_owned(),
        ]
    }
}

pub fn build_multimodal_request(frames: &[PreparedFrame]) -> Result<Value, VisionError> {
    if !(1..=4).contains(&frames.len()) || frames.iter().any(|frame| frame.jpeg.is_empty()) {
        return Err(VisionError::InvalidSchema);
    }
    let mut content = vec![json!({"type": "text", "text": VISION_PROMPT})];
    for frame in frames {
        let encoded = base64::engine::general_purpose::STANDARD.encode(&frame.jpeg);
        content.push(json!({
            "type": "image_url",
            "image_url": {"url": format!("data:image/jpeg;base64,{encoded}")}
        }));
    }
    Ok(json!({
        "model": "qwen3-vl-2b-instruct",
        "messages": [{"role": "user", "content": content}],
        "temperature": 0.1,
        "max_tokens": 512,
        "stream": false,
        "response_format": {
            "type": "json_object",
            "schema": {
                "type": "object",
                "additionalProperties": false,
                "required": [
                    "scene", "event", "confidence", "novelty",
                    "comment_worthiness", "visible_text"
                ],
                "properties": {
                    "scene": {"type": "string", "maxLength": 500},
                    "event": {"type": "string"},
                    "confidence": {"type": "number"},
                    "novelty": {"type": "number"},
                    "comment_worthiness": {"type": "number"},
                    "visible_text": {
                        "type": "array",
                        "maxItems": 16,
                        "items": {"type": "string", "maxLength": 200}
                    }
                }
            }
        }
    }))
}

pub fn validate_observation(observation: &VisionObservation) -> Result<(), VisionError> {
    let valid_score = |score: f32| score.is_finite() && (0.0..=1.0).contains(&score);
    if observation.scene.chars().count() > 500
        || observation.event.chars().count() > 2_000
        || !valid_score(observation.confidence)
        || !valid_score(observation.novelty)
        || !valid_score(observation.comment_worthiness)
        || observation.visible_text.len() > 16
        || observation
            .visible_text
            .iter()
            .any(|text| text.chars().count() > 200)
    {
        return Err(VisionError::InvalidSchema);
    }
    Ok(())
}

pub fn parse_completion(payload: &Value) -> Result<VisionObservation, VisionError> {
    let content = payload
        .get("choices")
        .and_then(Value::as_array)
        .and_then(|choices| choices.first())
        .and_then(|choice| choice.get("message"))
        .and_then(|message| message.get("content"))
        .and_then(Value::as_str)
        .ok_or(VisionError::InvalidSchema)?;
    let observation: VisionObservation =
        serde_json::from_str(content).map_err(|_| VisionError::InvalidSchema)?;
    validate_observation(&observation)?;
    Ok(observation)
}

pub trait VisionAnalyzer: Send {
    fn analyze(&mut self, frames: &[PreparedFrame]) -> Result<VisionObservation, VisionError>;
    fn stop(&mut self);
}

pub struct LlamaVisionRuntime {
    config: VisionServerConfig,
    child: Option<Child>,
    client: reqwest::blocking::Client,
}

impl LlamaVisionRuntime {
    pub fn start(
        config: VisionServerConfig,
        assign_child: &dyn Fn(&Child) -> io::Result<()>,
    ) -> Result<Self, VisionError> {
        Self::start_cancellable(config, assign_child, &|| false)
    }

    pub fn start_cancellable(
        mut config: VisionServerConfig,
        assign_child: &dyn Fn(&Child) -> io::Result<()>,
        is_cancelled: &dyn Fn() -> bool,
    ) -> Result<Self, VisionError> {
        if is_cancelled() {
            return Err(VisionError::Cancelled);
        }
        validate_assets(&config)?;
        config.port = available_loopback_port(config.port)?;
        let client = reqwest::blocking::Client::builder()
            .timeout(HTTP_TIMEOUT)
            .build()
            .map_err(|_| VisionError::ServerStart)?;
        let mut command = Command::new(&config.executable);
        command
            .args(config.args())
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x0800_0000);
        }
        let mut child = command.spawn().map_err(|_| VisionError::ServerStart)?;
        if assign_child(&child).is_err() {
            terminate_child(&mut child);
            return Err(VisionError::ServerStart);
        }
        if let Err(error) = wait_for_health(&client, &config, &mut child, is_cancelled) {
            terminate_child(&mut child);
            return Err(error);
        }
        Ok(Self {
            config,
            child: Some(child),
            client,
        })
    }

    #[cfg(test)]
    fn for_endpoint(port: u16) -> Self {
        Self {
            config: VisionServerConfig::for_project(Path::new("."), port),
            child: None,
            client: reqwest::blocking::Client::builder()
                .timeout(HTTP_TIMEOUT)
                .build()
                .expect("test HTTP client"),
        }
    }
}

impl VisionAnalyzer for LlamaVisionRuntime {
    fn analyze(&mut self, frames: &[PreparedFrame]) -> Result<VisionObservation, VisionError> {
        if let Some(child) = self.child.as_mut() {
            if child
                .try_wait()
                .map_err(|_| VisionError::ServerStart)?
                .is_some()
            {
                return Err(VisionError::ServerStart);
            }
        }
        let request = build_multimodal_request(frames)?;
        let url = format!("http://127.0.0.1:{}/v1/chat/completions", self.config.port);
        let response = self
            .client
            .post(url)
            .timeout(HTTP_TIMEOUT)
            .json(&request)
            .send()
            .map_err(|_| VisionError::Http)?;
        if !response.status().is_success() {
            return Err(VisionError::Http);
        }
        let payload = response
            .json::<Value>()
            .map_err(|_| VisionError::InvalidSchema)?;
        parse_completion(&payload)
    }

    fn stop(&mut self) {
        if let Some(mut child) = self.child.take() {
            terminate_child(&mut child);
        }
    }
}

impl Drop for LlamaVisionRuntime {
    fn drop(&mut self) {
        self.stop();
    }
}

fn validate_assets(config: &VisionServerConfig) -> Result<(), VisionError> {
    if !config.executable.is_file()
        || file_size(&config.model)? != MODEL_BYTES
        || file_size(&config.mmproj)? != MMPROJ_BYTES
    {
        return Err(VisionError::MissingAsset);
    }
    Ok(())
}

fn file_size(path: &Path) -> Result<u64, VisionError> {
    path.metadata()
        .map(|metadata| metadata.len())
        .map_err(|_| VisionError::MissingAsset)
}

fn available_loopback_port(preferred: u16) -> Result<u16, VisionError> {
    let address = SocketAddrV4::new(Ipv4Addr::LOCALHOST, preferred);
    let listener = TcpListener::bind(address)
        .or_else(|_| TcpListener::bind(SocketAddrV4::new(Ipv4Addr::LOCALHOST, 0)));
    listener
        .and_then(|listener| listener.local_addr().map(|address| address.port()))
        .map_err(|_| VisionError::ServerStart)
}

fn wait_for_health(
    client: &reqwest::blocking::Client,
    config: &VisionServerConfig,
    child: &mut Child,
    is_cancelled: &dyn Fn() -> bool,
) -> Result<(), VisionError> {
    let url = format!("http://127.0.0.1:{}/health", config.port);
    let deadline = Instant::now() + HEALTH_TIMEOUT;
    while Instant::now() < deadline {
        if is_cancelled() {
            return Err(VisionError::Cancelled);
        }
        if child
            .try_wait()
            .map_err(|_| VisionError::ServerStart)?
            .is_some()
        {
            return Err(VisionError::ServerStart);
        }
        if client
            .get(&url)
            .timeout(Duration::from_millis(500))
            .send()
            .is_ok_and(|response| response.status().is_success())
        {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(100));
    }
    Err(VisionError::ServerStart)
}

fn terminate_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::path::Path;
    use std::process::{Command, Stdio};
    use std::sync::Arc;
    use std::thread;
    use std::time::{Duration, Instant};

    fn prepared_jpeg(bytes: &[u8]) -> PreparedFrame {
        PreparedFrame {
            sequence: 1,
            captured_at_unix_ms: 123,
            jpeg: Arc::from(bytes),
            fingerprint: Arc::from([0_u8; 64 * 36]),
        }
    }

    fn completion(content: &str) -> serde_json::Value {
        serde_json::json!({
            "choices": [{"message": {"content": content}}]
        })
    }

    fn serve_once(response_body: Vec<u8>) -> (u16, thread::JoinHandle<Vec<u8>>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(2)))
                .unwrap();
            let mut request = Vec::new();
            let mut chunk = [0_u8; 4_096];
            loop {
                let read = stream.read(&mut chunk).unwrap();
                if read == 0 {
                    break;
                }
                request.extend_from_slice(&chunk[..read]);
                let Some(header_end) = request.windows(4).position(|part| part == b"\r\n\r\n")
                else {
                    continue;
                };
                let headers = String::from_utf8_lossy(&request[..header_end]);
                let content_length = headers
                    .lines()
                    .find_map(|line| {
                        let (name, value) = line.split_once(':')?;
                        name.eq_ignore_ascii_case("content-length")
                            .then(|| value.trim().parse::<usize>().ok())
                            .flatten()
                    })
                    .unwrap_or(0);
                if request.len() >= header_end + 4 + content_length {
                    break;
                }
            }
            write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                response_body.len()
            )
            .unwrap();
            stream.write_all(&response_body).unwrap();
            request
        });
        (port, handle)
    }

    #[test]
    fn server_command_uses_pinned_assets_loopback_and_single_slot() {
        let config = VisionServerConfig::for_project(Path::new(r"D:\LLM"), 18111);
        let args = config.args();
        assert!(args
            .windows(2)
            .any(|pair| pair[0] == "--host" && pair[1] == "127.0.0.1"));
        assert!(args
            .windows(2)
            .any(|pair| pair[0] == "--parallel" && pair[1] == "1"));
        assert!(args
            .windows(2)
            .any(|pair| pair[0] == "--ctx-size" && pair[1] == "2048"));
        assert!(args
            .windows(2)
            .any(|pair| pair[0] == "--image-max-tokens" && pair[1] == "96"));
        assert!(args.iter().any(|value| value == "--mmproj"));
        assert!(args.iter().any(|value| value == "--no-webui"));
        assert!(!args.iter().any(|value| value == "0.0.0.0"));
    }

    #[test]
    fn request_contains_only_data_urls_and_json_schema() {
        let body = build_multimodal_request(&[prepared_jpeg(b"jpeg")]).unwrap();
        let encoded = serde_json::to_string(&body).unwrap();
        assert!(encoded.contains("data:image/jpeg;base64,"));
        assert!(encoded.contains("comment_worthiness"));
        assert_eq!(body["max_tokens"], 512);
        assert!(!encoded.contains("http://") && !encoded.contains("https://"));
    }

    #[test]
    fn request_rejects_batches_outside_one_to_four_frames() {
        assert_eq!(
            build_multimodal_request(&[]).unwrap_err(),
            VisionError::InvalidSchema
        );
        let frames = (0..5).map(|_| prepared_jpeg(b"jpeg")).collect::<Vec<_>>();
        assert_eq!(
            build_multimodal_request(&frames).unwrap_err(),
            VisionError::InvalidSchema
        );
    }

    #[test]
    fn cancelled_start_never_touches_assets_or_spawns_a_child() {
        let config = VisionServerConfig::for_project(Path::new("missing"), 18111);
        assert!(matches!(
            LlamaVisionRuntime::start_cancellable(config, &|_| Ok(()), &|| true),
            Err(VisionError::Cancelled)
        ));
    }

    #[test]
    fn typed_completion_parses_exact_supported_schema() {
        let payload = completion(
            r#"{"scene":"game","event":"miss","confidence":0.9,"novelty":0.8,"comment_worthiness":0.7,"visible_text":["score"]}"#,
        );

        let observation = parse_completion(&payload).unwrap();

        assert_eq!(observation.scene, "game");
        assert_eq!(observation.visible_text, vec!["score"]);
    }

    #[test]
    fn typed_completion_rejects_non_finite_scores_and_unknown_fields() {
        let unknown = completion(
            r#"{"scene":"game","event":"miss","confidence":0.9,"novelty":0.8,"comment_worthiness":0.7,"visible_text":[],"extra":true}"#,
        );
        assert_eq!(
            parse_completion(&unknown).unwrap_err(),
            VisionError::InvalidSchema
        );

        let mut observation = VisionObservation {
            scene: "game".to_owned(),
            event: "miss".to_owned(),
            confidence: f32::NAN,
            novelty: 0.8,
            comment_worthiness: 0.7,
            visible_text: Vec::new(),
        };
        assert_eq!(
            validate_observation(&observation).unwrap_err(),
            VisionError::InvalidSchema
        );
        observation.confidence = 0.9;
        observation.visible_text = vec!["x".repeat(201)];
        assert_eq!(
            validate_observation(&observation).unwrap_err(),
            VisionError::InvalidSchema
        );
    }

    #[test]
    fn analyze_posts_only_to_loopback_without_authorization_and_parses_reply() {
        let response = completion(
            r#"{"scene":"video","event":"scene changed","confidence":0.9,"novelty":0.8,"comment_worthiness":0.7,"visible_text":[]}"#,
        );
        let (port, server) = serve_once(serde_json::to_vec(&response).unwrap());
        let mut runtime = LlamaVisionRuntime::for_endpoint(port);

        let observation = runtime.analyze(&[prepared_jpeg(b"jpeg")]).unwrap();
        let request = String::from_utf8(server.join().unwrap()).unwrap();

        assert_eq!(observation.scene, "video");
        assert!(request.starts_with("POST /v1/chat/completions HTTP/1.1"));
        assert!(request.contains(&format!("host: 127.0.0.1:{port}")));
        assert!(!request.to_ascii_lowercase().contains("authorization:"));
        assert!(request.contains("data:image/jpeg;base64,anBlZw=="));
    }

    #[test]
    fn malformed_completion_fails_without_retrying() {
        let response = completion("not json");
        let (port, server) = serve_once(serde_json::to_vec(&response).unwrap());
        let mut runtime = LlamaVisionRuntime::for_endpoint(port);

        assert_eq!(
            runtime.analyze(&[prepared_jpeg(b"jpeg")]).unwrap_err(),
            VisionError::InvalidSchema
        );
        let request = server.join().unwrap();
        assert_eq!(
            request
                .windows(b"POST /v1/chat/completions".len())
                .filter(|part| *part == b"POST /v1/chat/completions")
                .count(),
            1
        );
    }

    #[cfg(windows)]
    #[test]
    fn stop_kills_and_waits_for_owned_child() {
        use std::os::windows::process::CommandExt;

        let child = Command::new("cmd.exe")
            .args(["/C", "ping -n 30 127.0.0.1 >NUL"])
            .creation_flags(0x0800_0000)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let mut runtime = LlamaVisionRuntime::for_endpoint(18111);
        runtime.child = Some(child);

        let started = Instant::now();
        runtime.stop();

        assert!(runtime.child.is_none());
        assert!(started.elapsed() < Duration::from_secs(2));
    }
}
