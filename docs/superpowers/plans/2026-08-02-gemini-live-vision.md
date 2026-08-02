# Gemini Live Vision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm vision engine Gemini Live API (WebSocket BidiGenerateContent) cho screen awareness — auto chọn khi có API key, fallback local Qwen sau 3 lỗi liên tiếp.

**Architecture:** `GeminiLiveVisionRuntime` implement trait `VisionAnalyzer` (vision_runtime.rs:167-170). `spawn_vision_worker` (observation_runtime.rs:497) hỏi bridge `GET /observation/vision-config` để chọn engine. Key lưu trong OS keyring qua Python `CredentialStore` (pattern `core.kilo_cred`), chỉ xuất hiện trong RAM Rust trong phiên observation.

**Tech Stack:** Rust (crate chạy blocking threads, `reqwest` blocking đã có), `tungstenite` blocking client (thay `tokio-tungstenite` — crate KHÔNG có tokio runtime, toàn bộ observation pipeline là blocking threads; deviation từ spec), Python FastAPI + keyring, React/Vite (SettingsTab).

## Global Constraints

- Mọi lỗi log KHÔNG được chứa API key (log `{error:?}` của `VisionError::Gemini(String)` chỉ chứa mô tả).
- `GET /settings` KHÔNG trả key — giữ test `test_settings_round_trip_and_no_api_key`.
- Model mặc định `gemini-2.5-flash-live-preview`; nếu live-probe lỗi 1011 với TEXT modality thì thử `gemini-2.0-flash-live-001` (Task 5 quyết định, đổi const duy nhất).
- Fallback local là VĨNH VIỄN trong phiên observation (không nhảy qua lại), reset khi observation tắt/bật lại.
- Frame prepared 480×270 JPEG (`PreparedFrame.jpeg`) — engine-agnostic, không đổi.
- Rust edition 2021, `cargo fmt` + `cargo clippy` sạch, 0 warning.
- Python: `pytest tests/test_server_api.py` pass, không thêm dependency Python mới.
- Frontend: `npx vitest run` pass, `npm run build` pass.

---

### Task 1: Backend Python — lưu Gemini vision key + endpoint `/observation/vision-config`

**Files:**
- Modify: `D:\LLM\src\mini_gpt\backend_core.py:18-19` (thêm CredentialStore thứ 3)
- Modify: `D:\LLM\src\mini_gpt\server.py` (SettingsPayload dòng ~631-644, POST /settings dòng ~867-926, thêm endpoint mới)
- Test: `D:\LLM\tests\test_server_api.py`

**Interfaces:**
- Produces: `core.gemini_vision_cred` (`CredentialStore("mini_gpt", "gemini_vision_api_key")`); endpoint `GET /observation/vision-config` trả `{"status": "ok", "engine": "gemini"|"local", "api_key": str|None}`; `POST /settings` nhận field `gemini_vision_api_key` (max 4096).

- [ ] **Step 1: Viết test fail trước** — thêm vào `tests/test_server_api.py` (sau `test_settings_saves_kilo_api_key`):

```python
def test_settings_saves_gemini_vision_key(monkeypatch, isolated_state) -> None:
    saved = []
    monkeypatch.setattr(
        server.core, "gemini_vision_cred",
        type("Fake", (), {
            "set_key": lambda self, key: saved.append(key),
            "get_key": lambda self: saved[0] if saved else None,
        })(),
    )
    with TestClient(server.app) as client:
        response = client.post("/settings", json={"gemini_vision_api_key": "gem-key-123"})
        assert response.status_code == 200
        body = client.get("/settings").json()
        assert "gemini_vision_api_key" not in body
        config = client.get("/observation/vision-config").json()
    assert saved == ["gem-key-123"]
    assert config == {"status": "ok", "engine": "gemini", "api_key": "gem-key-123"}


def test_observation_vision_config_local_without_key(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(
        server.core, "gemini_vision_cred",
        type("Fake", (), {"set_key": lambda self, key: None, "get_key": lambda self: None})(),
    )
    with TestClient(server.app) as client:
        config = client.get("/observation/vision-config").json()
    assert config == {"status": "ok", "engine": "local", "api_key": None}


def test_settings_empty_gemini_key_does_not_crash(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(
        server.core, "gemini_vision_cred",
        type("Fake", (), {"set_key": lambda self, key: calls.append(key), "get_key": lambda self: None})(),
    )
    with TestClient(server.app) as client:
        response = client.post("/settings", json={"gemini_vision_api_key": ""})
    assert response.status_code == 200
    assert calls == []
```

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `.venv\Scripts\python.exe -m pytest tests/test_server_api.py::test_settings_saves_gemini_vision_key -q --tb=short` (workdir `D:\LLM`)
Expected: FAIL — `AttributeError: 'MiniGPTCore' object has no attribute 'gemini_vision_cred'`

- [ ] **Step 3: Implement** — `backend_core.py:18-19` thêm dòng:

```python
        self.gemini_vision_cred = CredentialStore("mini_gpt", "gemini_vision_api_key")
```

`server.py` — `SettingsPayload` (sau dòng `voice_speed`):

```python
    gemini_vision_api_key: str = Field(default="", max_length=4096)
```

`server.py` — trong `save_settings`, NGAY SAU `save_settings_to_file(settings)` (dòng ~896) và TRƯỚC `if payload.backend is None:`:

```python
    if payload.gemini_vision_api_key.strip():
        try:
            core.gemini_vision_cred.set_key(payload.gemini_vision_api_key)
        except Exception as error:
            logger.error("Failed to save the Gemini vision API key: %s", error)
            return {"status": "error", "message": str(error)}
```

`server.py` — endpoint mới, đặt ngay SAU `@app.post("/observation/preflight")` handler (sau dòng ~745):

```python
@app.get("/observation/vision-config")
def observation_vision_config():
    try:
        key = core.gemini_vision_cred.get_key() or ""
    except Exception as error:
        logger.warning("Could not read the Gemini vision API key: %s", error)
        key = ""
    if key.strip():
        return {"status": "ok", "engine": "gemini", "api_key": key}
    return {"status": "ok", "engine": "local", "api_key": None}
```

- [ ] **Step 4: Chạy test xác nhận pass**

Run: `.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q --tb=short`
Expected: PASS, tất cả test server_api (kể cả cũ) xanh.

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/backend_core.py src/mini_gpt/server.py tests/test_server_api.py
git commit -m "feat(observation): store gemini vision key and expose vision-config endpoint"
```

---

### Task 2: Rust — `VisionError::Gemini` + `GeminiLiveVisionRuntime`

**Files:**
- Modify: `D:\LLM\gui-tauri\src-tauri\Cargo.toml`
- Modify: `D:\LLM\gui-tauri\src-tauri\src\vision_runtime.rs` (VisionError enum + `VISION_PROMPT` visibility)
- Create: `D:\LLM\gui-tauri\src-tauri\src\gemini_vision_runtime.rs`
- Modify: `D:\LLM\gui-tauri\src-tauri\src\lib.rs` (mod khai báo)

**Interfaces:**
- Consumes: `VisionAnalyzer` trait, `VisionObservation`, `validate_observation`, `PreparedFrame`, `VISION_PROMPT` (từ vision_runtime.rs).
- Produces: `GeminiLiveVisionRuntime::connect(api_key: &str, is_cancelled: &dyn Fn() -> bool) -> Result<Self, VisionError>`; `build_setup_message(model: &str) -> serde_json::Value`; `build_client_content_message(frames: &[PreparedFrame]) -> Result<serde_json::Value, VisionError>`; `enum ServerEvent { Text(String), TurnComplete, Interrupted, SetupComplete, Error(String) }` + `parse_server_message(&Value) -> Option<ServerEvent>`; `VisionError::Gemini(String)`.

- [ ] **Step 1: Thêm dependency**

Run: `cargo add tungstenite --no-default-features --features rustls-tls` (workdir `D:\LLM\gui-tauri\src-tauri`)
Expected: `Adding tungstenite v0.2x` — Cargo.toml có dòng tungstenite.

- [ ] **Step 2: Viết test fail trước** — tạo `gemini_vision_runtime.rs` với module tests; đăng ký `mod gemini_vision_runtime;` trong `lib.rs`. Test các hàm thuần (không cần network):

```rust
#[cfg(test)]
mod tests {
    use super::*;
    use crate::frame_buffer::PreparedFrame;
    use crate::vision_runtime::validate_observation;
    use std::sync::Arc;

    fn prepared_jpeg(bytes: &[u8]) -> PreparedFrame {
        PreparedFrame {
            sequence: 1,
            captured_at_unix_ms: 123,
            jpeg: Arc::from(bytes),
            fingerprint: Arc::from([0_u8; 64 * 36]),
        }
    }

    #[test]
    fn setup_message_pins_model_and_text_modality() {
        let setup = build_setup_message(GEMINI_LIVE_MODEL);
        assert_eq!(setup["setup"]["model"], format!("models/{GEMINI_LIVE_MODEL}"));
        assert_eq!(
            setup["setup"]["generationConfig"]["responseModalities"][0],
            "TEXT"
        );
        assert_eq!(setup["setup"]["generationConfig"]["temperature"], 0.1);
        assert!(setup["setup"]["systemInstruction"]["parts"][0]["text"]
            .as_str()
            .unwrap()
            .contains("JSON"));
    }

    #[test]
    fn client_content_embeds_jpeg_base64_and_schema_prompt() {
        let message = build_client_content_message(&[prepared_jpeg(b"jpeg")]).unwrap();
        let encoded = serde_json::to_string(&message).unwrap();
        assert!(encoded.contains("image/jpeg"));
        assert!(encoded.contains("anBlZw=="));
        assert!(encoded.contains("comment_worthiness"));
        assert!(encoded.contains("clientContent"));
    }

    #[test]
    fn client_content_rejects_zero_and_five_frames() {
        assert_eq!(
            build_client_content_message(&[]).unwrap_err(),
            VisionError::InvalidSchema
        );
        let frames = (0..5).map(|_| prepared_jpeg(b"jpeg")).collect::<Vec<_>>();
        assert_eq!(
            build_client_content_message(&frames).unwrap_err(),
            VisionError::InvalidSchema
        );
    }

    #[test]
    fn server_messages_map_to_events() {
        let text = serde_json::json!({"serverContent": {"modelTurn": {"parts": [{"text": "{\"scene\":"}]}}});
        assert!(matches!(
            parse_server_message(&text),
            Some(ServerEvent::Text(part)) if part.contains("scene")
        ));
        let complete = serde_json::json!({"serverContent": {"turnComplete": true}});
        assert!(matches!(parse_server_message(&complete), Some(ServerEvent::TurnComplete)));
        let interrupted = serde_json::json!({"serverContent": {"interrupted": true}});
        assert!(matches!(parse_server_message(&interrupted), Some(ServerEvent::Interrupted)));
        let setup = serde_json::json!({"setupComplete": {}});
        assert!(matches!(parse_server_message(&setup), Some(ServerEvent::SetupComplete)));
        let error = serde_json::json!({"error": {"message": "quota exceeded"}});
        assert!(matches!(
            parse_server_message(&error),
            Some(ServerEvent::Error(msg)) if msg.contains("quota")
        ));
        assert!(parse_server_message(&serde_json::json!({"foo": 1})).is_none());
    }

    #[test]
    fn streamed_text_parses_into_observation() {
        let text = r#"{"scene":"game","event":"miss","confidence":0.9,"novelty":0.8,"comment_worthiness":0.7,"visible_text":["score"]}"#;
        let observation: VisionObservation = serde_json::from_str(text).unwrap();
        validate_observation(&observation).unwrap();
        assert_eq!(observation.scene, "game");
    }
}
```

- [ ] **Step 3: Chạy test xác nhận fail**

Run: `cargo test gemini_vision_runtime` (workdir `D:\LLM\gui-tauri\src-tauri`)
Expected: FAIL — không compile (thiếu module/functions). Đây là lần fail hợp lệ đầu tiên; sau đó implement cho compile.

- [ ] **Step 4: Implement** — `vision_runtime.rs`:

Đổi `const VISION_PROMPT: &str` (dòng 16) thành `pub(crate) const VISION_PROMPT: &str`; đổi derive của `VisionError` (dòng 29-36):

```rust
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VisionError {
    MissingAsset,
    ServerStart,
    Http,
    InvalidSchema,
    Cancelled,
    Gemini(String),
}
```

(bỏ `Copy` — `Gemini(String)` không Copy được; toàn bộ usage trong codebase là pattern-match, không cần Copy.)

Tạo `gemini_vision_runtime.rs`:

```rust
use crate::frame_buffer::PreparedFrame;
use crate::vision_runtime::{validate_observation, VisionError, VisionObservation, VisionAnalyzer, VISION_PROMPT};
use base64::Engine;
use serde_json::{json, Value};
use std::io;
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};
use tungstenite::client::client;
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Error as WsError, Message, WebSocket};

const GEMINI_LIVE_MODEL: &str = "gemini-2.5-flash-live-preview";
const GEMINI_WS_HOST: &str = "generativelanguage.googleapis.com:443";
const GEMINI_WS_PATH: &str = "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
const TURN_TIMEOUT: Duration = Duration::from_secs(10);
const READ_TICK: Duration = Duration::from_secs(1);
const GEMINI_SCHEMA_PROMPT: &str = r#"
Return exactly this JSON object, no markdown fences, no extra text:
{"scene": string(<=500), "event": string, "confidence": 0..1, "novelty": 0..1, "comment_worthiness": 0..1, "visible_text": [string(<=200), <=16 items]}
No other keys."#;

pub(crate) fn build_setup_message(model: &str) -> Value {
    json!({
        "setup": {
            "model": format!("models/{model}"),
            "generationConfig": {
                "candidateCount": 1,
                "responseModalities": ["TEXT"],
                "temperature": 0.1,
                "maxOutputTokens": 512
            },
            "systemInstruction": {
                "parts": [{"text": format!("{VISION_PROMPT}{GEMINI_SCHEMA_PROMPT}")}]
            }
        }
    })
}

pub(crate) fn build_client_content_message(frames: &[PreparedFrame]) -> Result<Value, VisionError> {
    if !(1..=4).contains(&frames.len()) || frames.iter().any(|frame| frame.jpeg.is_empty()) {
        return Err(VisionError::InvalidSchema);
    }
    let mut parts = Vec::new();
    for frame in frames {
        let encoded = base64::engine::general_purpose::STANDARD.encode(&frame.jpeg);
        parts.push(json!({"inlineData": {"mimeType": "image/jpeg", "data": encoded}}));
    }
    parts.push(json!({"text": VISION_PROMPT}));
    Ok(json!({
        "clientContent": {"turns": [{"role": "user", "parts": parts}]}
    }))
}

pub(crate) enum ServerEvent {
    Text(String),
    TurnComplete,
    Interrupted,
    SetupComplete,
    Error(String),
}

pub(crate) fn parse_server_message(payload: &Value) -> Option<ServerEvent> {
    if let Some(error) = payload.get("error").and_then(Value::as_object) {
        let message = error
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("unknown error")
            .to_owned();
        return Some(ServerEvent::Error(message));
    }
    if payload.get("setupComplete").is_some() {
        return Some(ServerEvent::SetupComplete);
    }
    let content = payload.get("serverContent")?;
    if content.get("interrupted").and_then(Value::as_bool) == Some(true) {
        return Some(ServerEvent::Interrupted);
    }
    let mut text = String::new();
    if let Some(parts) = content.get("modelTurn").and_then(|turn| turn.get("parts")).and_then(Value::as_array) {
        for part in parts {
            if let Some(chunk) = part.get("text").and_then(Value::as_str) {
                text.push_str(chunk);
            }
        }
    }
    if content.get("turnComplete").and_then(Value::as_bool) == Some(true) {
        if !text.is_empty() {
            return Some(ServerEvent::Text(text));
        }
        return Some(ServerEvent::TurnComplete);
    }
    if !text.is_empty() {
        return Some(ServerEvent::Text(text));
    }
    None
}

pub struct GeminiLiveVisionRuntime {
    socket: WebSocket<MaybeTlsStream<TcpStream>>,
    api_key: String,
}

impl GeminiLiveVisionRuntime {
    pub fn connect(
        api_key: &str,
        is_cancelled: &dyn Fn() -> bool,
    ) -> Result<Self, VisionError> {
        if is_cancelled() {
            return Err(VisionError::Cancelled);
        }
        let address = GEMINI_WS_HOST
            .to_socket_addrs()
            .map_err(|_| VisionError::Gemini("dns".into()))?
            .next()
            .ok_or_else(|| VisionError::Gemini("dns".into()))?;
        let stream = connect_tcp_with_timeout(address, is_cancelled)?;
        stream
            .set_read_timeout(Some(READ_TICK))
            .map_err(|_| VisionError::Gemini("timeout setup".into()))?;
        let url = format!("wss://{GEMINI_WS_HOST}{GEMINI_WS_PATH}?key={api_key}");
        let (socket, _) = client(&url, stream).map_err(|error| VisionError::Gemini(format!("connect: {error}")))?;
        let mut runtime = Self {
            socket,
            api_key: api_key.to_owned(),
        };
        runtime.send_setup(is_cancelled)?;
        Ok(runtime)
    }

    fn send_setup(&mut self, is_cancelled: &dyn Fn() -> bool) -> Result<(), VisionError> {
        let setup = build_setup_message(GEMINI_LIVE_MODEL);
        self.socket
            .send(Message::Text(setup.to_string()))
            .map_err(|error| VisionError::Gemini(format!("setup send: {error}")))?;
        let deadline = Instant::now() + CONNECT_TIMEOUT;
        loop {
            if is_cancelled() {
                return Err(VisionError::Cancelled);
            }
            if Instant::now() > deadline {
                return Err(VisionError::Gemini("setup timeout".into()));
            }
            match self.read_message(is_cancelled)? {
                Some(ServerEvent::SetupComplete) => return Ok(()),
                Some(ServerEvent::Error(message)) => return Err(VisionError::Gemini(message)),
                Some(_) => continue,
                None => continue,
            }
        }
    }

    fn read_message(&mut self, is_cancelled: &dyn Fn() -> bool) -> Result<Option<ServerEvent>, VisionError> {
        if is_cancelled() {
            return Err(VisionError::Cancelled);
        }
        match self.socket.read() {
            Ok(Message::Text(text)) => {
                let payload: Value = serde_json::from_str(&text)
                    .map_err(|_| VisionError::Gemini("unparseable message".into()))?;
                Ok(parse_server_message(&payload))
            }
            Ok(Message::Ping(payload)) => {
                self.socket
                    .send(Message::Pong(payload))
                    .map_err(|error| VisionError::Gemini(format!("pong: {error}")))?;
                Ok(None)
            }
            Ok(Message::Close(_)) => Err(VisionError::Gemini("closed by server".into())),
            Ok(_) => Ok(None),
            Err(WsError::Io(error))
                if error.kind() == io::ErrorKind::WouldBlock
                    || error.kind() == io::ErrorKind::TimedOut =>
            {
                Ok(None)
            }
            Err(error) => Err(VisionError::Gemini(format!("read: {error}"))),
        }
    }
}

fn connect_tcp_with_timeout(
    address: SocketAddr,
    is_cancelled: &dyn Fn() -> bool,
) -> Result<TcpStream, VisionError> {
    let deadline = Instant::now() + CONNECT_TIMEOUT;
    loop {
        if is_cancelled() {
            return Err(VisionError::Cancelled);
        }
        match TcpStream::connect_timeout(&address, CONNECT_TIMEOUT) {
            Ok(stream) => return Ok(stream),
            Err(_) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(500)),
            Err(_) => return Err(VisionError::Gemini("tcp connect".into())),
        }
    }
}

impl VisionAnalyzer for GeminiLiveVisionRuntime {
    fn analyze(&mut self, frames: &[PreparedFrame]) -> Result<VisionObservation, VisionError> {
        let message = build_client_content_message(frames)?;
        self.socket
            .send(Message::Text(message.to_string()))
            .map_err(|error| VisionError::Gemini(format!("turn send: {error}")))?;
        let deadline = Instant::now() + TURN_TIMEOUT;
        let mut text = String::new();
        loop {
            if Instant::now() > deadline {
                return Err(VisionError::Gemini("turn timeout".into()));
            }
            match self.read_message(&|| false)? {
                Some(ServerEvent::Text(chunk)) => text.push_str(&chunk),
                Some(ServerEvent::TurnComplete) => {
                    if text.trim().is_empty() {
                        return Err(VisionError::Gemini("empty turn".into()));
                    }
                    let observation: VisionObservation = serde_json::from_str(&text)
                        .map_err(|_| VisionError::InvalidSchema)?;
                    validate_observation(&observation)?;
                    return Ok(observation);
                }
                Some(ServerEvent::Interrupted) => {
                    return Err(VisionError::Gemini("interrupted".into()));
                }
                Some(ServerEvent::Error(message)) => return Err(VisionError::Gemini(message)),
                Some(ServerEvent::SetupComplete) | None => continue,
            }
        }
    }

    fn stop(&mut self) {
        let _ = self.socket.close(None);
    }
}

impl Drop for GeminiLiveVisionRuntime {
    fn drop(&mut self) {
        self.stop();
    }
}
```

- [ ] **Step 5: Chạy test xác nhận pass**

Run: `cargo test gemini_vision_runtime` và `cargo test vision_runtime` (workdir `D:\LLM\gui-tauri\src-tauri`)
Expected: PASS — 4 test mới + toàn bộ test vision_runtime cũ (bỏ Copy không phá test nào; nếu compile lỗi ở chỗ khác thì sửa theo compiler).

- [ ] **Step 6: fmt + clippy + commit**

Run: `cargo fmt && cargo clippy --all-targets` — sạch không warning.
```bash
git add src-tauri/Cargo.toml src-tauri/Cargo.lock src-tauri/src/gemini_vision_runtime.rs src-tauri/src/vision_runtime.rs src-tauri/src/lib.rs
git commit -m "feat(observation): gemini live vision runtime with text modality"
```

---

### Task 3: Rust — bridge `vision_config` + chọn engine + fallback + metrics

**Files:**
- Modify: `D:\LLM\gui-tauri\src-tauri\src\backend_bridge.rs`
- Modify: `D:\LLM\gui-tauri\src-tauri\src\observation_runtime.rs`
- Modify: `D:\LLM\gui-tauri\src\lib\observationClient.ts`
- Modify: `D:\LLM\gui-tauri\src\components\ObservationControl.tsx`
- Test: `D:\LLM\gui-tauri\src-tauri\src\backend_bridge.rs` (mod tests), `D:\LLM\gui-tauri\src\components\ObservationControl.test.tsx`

**Interfaces:**
- Consumes: `GeminiLiveVisionRuntime::connect(api_key, is_cancelled)`, `VisionEngine` (enum mới, serialize "local"/"gemini").
- Produces: `BackendBridge::vision_config(&self) -> Result<VisionConfig, BridgeError>`; `pub struct VisionConfig { pub engine: String, pub api_key: Option<String> }`; `ObservationMetricsSnapshot.vision_engine: VisionEngine`; TS `ObservationMetricsSnapshot.visionEngine: "local" | "gemini"`.

- [ ] **Step 1: Viết test fail trước** — `backend_bridge.rs` mod tests thêm:

```rust
#[test]
fn vision_config_returns_engine_and_key_from_loopback() {
    let (base_url, server) = serve_once(
        r#"{"status":"ok","engine":"gemini","api_key":"gem-key"}"#,
    );
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
```

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `cargo test vision_config_returns_engine_and_key` (workdir `D:\LLM\gui-tauri\src-tauri`)
Expected: FAIL — `no method named vision_config`.

- [ ] **Step 3: Implement** — `backend_bridge.rs`:

```rust
#[derive(Clone, Debug, Deserialize)]
pub struct VisionConfig {
    pub engine: String,
    #[serde(default)]
    pub api_key: Option<String>,
}
```

Trong trait `ReactionClient` (dòng 49-54) — KHÔNG thêm vào trait (giữ trait tối thiểu), thêm method riêng trên `BackendBridge`:

```rust
impl BackendBridge {
    pub fn vision_config(&self) -> Result<VisionConfig, BridgeError> {
        let response = self
            .client
            .get(format!("{}/observation/vision-config", self.base_url))
            .bearer_auth(&self.token)
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
```

- [ ] **Step 4: Chạy test xác nhận pass**

Run: `cargo test vision_config_returns_engine_and_key`
Expected: PASS.

- [ ] **Step 5: Viết test metrics fail trước** — `observation_runtime.rs` mod tests (chỗ test snapshot dòng ~1057) thêm field mới:

```rust
    #[test]
    fn metrics_snapshot_exposes_vision_engine() {
        let mut metrics = ObservationMetrics::default();
        metrics.vision_engine = VisionEngine::Gemini;
        let snapshot = metrics.snapshot(0);
        assert_eq!(snapshot.vision_engine, VisionEngine::Gemini);
    }
```

- [ ] **Step 6: Implement engine + fallback** — `observation_runtime.rs`:

Thêm enum + field (sau struct `ObservationSnapshot` dòng ~43):

```rust
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum VisionEngine {
    Local,
    Gemini,
}
```

`ObservationMetricsSnapshot` (dòng 60) thêm `pub vision_engine: VisionEngine,`; `ObservationMetrics` (dòng 96) thêm `vision_engine: VisionEngine,` (Default → Local); `snapshot()` (dòng 145) thêm `vision_engine: self.vision_engine.clone(),`; branch `unwrap_or` trong `metrics()` (dòng 206) thêm `vision_engine: VisionEngine::Local,`.

Thêm `use crate::gemini_vision_runtime::GeminiLiveVisionRuntime;` (đầu file).

`spawn_vision_worker` (dòng 492-530) — thay khối `let mut restart_count` + `let config = ...` + `let runtime = ...` thành:

```rust
        let mut preferred_engine = VisionEngine::Local;
        let mut gemini_key = None;
        if let Ok(config) = bridge.vision_config() {
            if config.engine == "gemini"
                && config.api_key.as_deref().is_some_and(|key| !key.trim().is_empty())
            {
                preferred_engine = VisionEngine::Gemini;
                gemini_key = Some(config.api_key.unwrap_or_default());
            }
        }
        let mut restart_count = 0_u8;
        let mut gemini_failures = 0_u8;
        loop {
            if stop.load(Ordering::Acquire) {
                return;
            }
            let mut vision: Box<dyn VisionAnalyzer> = match preferred_engine {
                VisionEngine::Gemini => {
                    let key = gemini_key.clone().unwrap_or_default();
                    let stop_guard = stop.clone();
                    match GeminiLiveVisionRuntime::connect(&key, &|| stop_guard.load(Ordering::Acquire)) {
                        Ok(runtime) => Box::new(runtime),
                        Err(VisionError::Cancelled) => return,
                        Err(error) => {
                            eprintln!("vision: gemini connect failed: {error:?}");
                            gemini_failures += 1;
                            if gemini_failures >= 3 {
                                eprintln!("vision: gemini failed {gemini_failures}x, falling back to local");
                                preferred_engine = VisionEngine::Local;
                                restart_count = 0;
                                if let Ok(mut value) = metrics.lock() {
                                    value.vision_engine = VisionEngine::Local;
                                }
                            }
                            if restart_count < 2 {
                                restart_count += 1;
                                set_restart_count(&snapshot, restart_count);
                                sleep_interruptible(&stop, if restart_count == 1 { 1 } else { 3 });
                                continue;
                            }
                            clear_frames(&ring, &buffered_bytes);
                            set_phase(
                                &snapshot,
                                &app,
                                ObservationPhase::Error,
                                "Gemini vision could not start.",
                            );
                            stop.store(true, Ordering::Release);
                            return;
                        }
                    }
                }
                VisionEngine::Local => {
                    let config = VisionServerConfig::for_project(&project_root, 18111);
                    let assign_app = app.clone();
                    match LlamaVisionRuntime::start_cancellable(
                        config,
                        &move |child| assign_app.state::<BackendState>().assign_owned_child(child),
                        &|| stop.load(Ordering::Acquire),
                    ) {
                        Ok(runtime) => Box::new(runtime),
                        Err(VisionError::Cancelled) => return,
                        Err(_) if restart_count < 2 => {
                            restart_count += 1;
                            set_restart_count(&snapshot, restart_count);
                            sleep_interruptible(&stop, if restart_count == 1 { 1 } else { 3 });
                            continue;
                        }
                        Err(_) => {
                            clear_frames(&ring, &buffered_bytes);
                            set_phase(
                                &snapshot,
                                &app,
                                ObservationPhase::Error,
                                "Local vision could not start.",
                            );
                            stop.store(true, Ordering::Release);
                            return;
                        }
                    }
                }
            };
            if let Ok(mut value) = metrics.lock() {
                value.vision_engine = preferred_engine.clone();
            }
            set_phase(
                &snapshot,
                &app,
                ObservationPhase::Observing,
                "Watching the primary monitor.",
            );
```

`analyze` error arm (dòng 593-615) — thay bằng:

```rust
                    Err(
                        VisionError::Gemini(_)
                        | VisionError::ServerStart
                        | VisionError::Http,
                    ) => {
                        vision.stop();
                        if preferred_engine == VisionEngine::Gemini {
                            gemini_failures += 1;
                            if gemini_failures >= 3 {
                                eprintln!(
                                    "vision: gemini failed {gemini_failures}x during analysis, falling back to local"
                                );
                                preferred_engine = VisionEngine::Local;
                                restart_count = 0;
                                if let Ok(mut value) = metrics.lock() {
                                    value.vision_engine = VisionEngine::Local;
                                }
                                break;
                            }
                        }
                        if restart_count < 2 {
                            restart_count += 1;
                            set_restart_count(&snapshot, restart_count);
                            sleep_interruptible(&stop, if restart_count == 1 { 1 } else { 3 });
                            break;
                        }
                        set_phase(
                            &snapshot,
                            &app,
                            ObservationPhase::Error,
                            if preferred_engine == VisionEngine::Gemini {
                                "Gemini vision stopped after repeated failures."
                            } else {
                                "Local vision stopped after repeated failures."
                            },
                        );
                        clear_frames(&ring, &buffered_bytes);
                        stop.store(true, Ordering::Release);
                        return;
                    }
```

(`restart_count` bây giờ không reset khi fallback sang local — chấp nhận: fallback là nỗ lực cuối, restart_count ≤ 2 tiếp tục đúng nghĩa "nỗ lực khởi động".)

- [ ] **Step 7: Cập nhật TS type + UI** — `observationClient.ts`:

```ts
export type ObservationMetricsSnapshot = {
  ...
  visionEngine: "local" | "gemini";
  ...
};
```

`ObservationControl.tsx` — `EMPTY_METRICS` thêm `visionEngine: "local",`; dòng detail (156) đổi `vision ${metrics.visionAnalyses}` thành `vision ${metrics.visionEngine}/${metrics.visionAnalyses}`; thêm `data-vision-engine={metrics.visionEngine}` gần data-vision-analyses.

- [ ] **Step 8: Cập nhật test TS** — `ObservationControl.test.tsx`: fixture `metrics` (dòng 29) thêm `visionEngine: "local",`; chuỗi assert dòng 117 đổi thành:

```tsx
"Đang nhìn · frame 40 · vision local/12 · ứng viên 4 · thấp c/n/w 0/8/10 · điểm cuối 0.91/0.42/0.38 · cloud a/s/f 2/1/0 · ms trước/cloud/sau 900/1800/2700 · stale 1 · đã phát 1",
```

- [ ] **Step 9: Chạy toàn bộ test**

Run (workdir `D:\LLM\gui-tauri\src-tauri`): `cargo test`
Run (workdir `D:\LLM\gui-tauri`): `npx vitest run`
Run (workdir `D:\LLM`): `.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q --tb=short`
Expected: tất cả xanh (cargo gồm test mới của Task 2+3; vitest 11+; pytest server_api).

- [ ] **Step 10: fmt + clippy + commit**

Run: `cargo fmt && cargo clippy --all-targets` — sạch.
```bash
git add src-tauri/src/backend_bridge.rs src-tauri/src/observation_runtime.rs src/observationClient.ts src/components/ObservationControl.tsx src/components/ObservationControl.test.tsx
git commit -m "feat(observation): auto-select gemini live vision with local fallback"
```

---

### Task 4: Settings UI — input Gemini Vision API key

**Files:**
- Modify: `D:\LLM\gui-tauri\src\components\SettingsTab.tsx`
- Create: `D:\LLM\gui-tauri\src\components\SettingsTab.test.tsx`

**Interfaces:**
- Consumes: `POST /settings {gemini_vision_api_key}` (Task 1).
- Produces: không có gì cho task khác — UI thuần.

- [ ] **Step 1: Viết test fail trước** — tạo `SettingsTab.test.tsx`:

```tsx
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SettingsTab from "./SettingsTab";

const getSettings = vi.hoisted(() => vi.fn());
const postSettings = vi.hoisted(() => vi.fn());

beforeEach(() => {
  vi.clearAllMocks();
  getSettings.mockResolvedValue({
    ok: true,
    json: () =>
      Promise.resolve({
        status: "ok",
        backend: "llama.cpp",
        model: "local-model-gguf",
        api_variant: "Default",
        max_tokens: 1024,
        temperature: 0.7,
        top_k: 50,
        allow_web_search: true,
        enable_self_learning: true,
        share_memory_with_cloud: false,
        checkpoint_path: "",
      }),
  });
  postSettings.mockResolvedValue({ ok: true, json: () => Promise.resolve({ status: "success", message: "ok" }) });
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string | URL, init?: RequestInit) => {
      if (init?.method === "POST") return postSettings(url);
      return getSettings(url);
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("SettingsTab Gemini vision key", () => {
  it("sends the gemini vision key and clears the input after save", async () => {
    render(<SettingsTab />);
    const input = await screen.findByLabelText("Gemini Vision API Key");
    fireEvent.change(input, { target: { value: "gem-key-abc" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply Settings" }));

    await waitFor(() =>
      expect(postSettings).toHaveBeenCalledWith(
        expect.anything(),
        expect.objectContaining({
          body: expect.stringContaining("gemini_vision_api_key"),
        }),
      ),
    );
    const body = JSON.parse((postSettings.mock.calls[0][1] as RequestInit).body as string);
    expect(body.gemini_vision_api_key).toBe("gem-key-abc");
    await waitFor(() => expect(input).toHaveValue(""));
  });
});
```

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `npx vitest run src/components/SettingsTab.test.tsx` (workdir `D:\LLM\gui-tauri`)
Expected: FAIL — không có input "Gemini Vision API Key".

- [ ] **Step 3: Implement** — `SettingsTab.tsx`: thêm state + input + gửi + clear:

```tsx
const [geminiVisionApiKey, setGeminiVisionApiKey] = useState("");
```

Trong `saveSettings` body (dòng 123-135), thêm `gemini_vision_api_key: geminiVisionApiKey,`; trong nhánh `data.status === "success"` thêm `setGeminiVisionApiKey("");`.

Trong JSX, đặt ngay SAU block API Key (sau dòng ~197):

```tsx
            <div className="form-group">
              <label>Gemini Vision API Key</label>
              <input
                type="password"
                value={geminiVisionApiKey}
                onChange={(e) => setGeminiVisionApiKey(e.target.value)}
                placeholder="AIza..."
              />
              <p style={{ color: "var(--text-secondary)", marginTop: 4, fontSize: 13 }}>
                Screen observation uses Gemini Live automatically when this key is saved.
              </p>
            </div>
```

- [ ] **Step 4: Chạy test xác nhận pass**

Run: `npx vitest run` (workdir `D:\LLM\gui-tauri`)
Expected: PASS — 12 test (11 cũ + 1 mới).

- [ ] **Step 5: Build + commit**

Run: `npm run build` — thành công.
```bash
git add src/components/SettingsTab.tsx src/components/SettingsTab.test.tsx
git commit -m "feat(ui): gemini vision api key input in settings"
```

---

### Task 5: Live probe + end-to-end verification (chạy cùng chủ nhân)

**Files:**
- Create (tạm, không commit): `D:\LLM\scripts\probe_gemini_vision.py`

**Interfaces:**
- Consumes: key trong keyring (Task 1) — script đọc qua `CredentialStore("mini_gpt", "gemini_vision_api_key")` hoặc env `GEMINI_KEY`.

- [ ] **Step 1: Viết probe script** — `scripts/probe_gemini_vision.py`:

```python
"""Live probe: gửi 1 frame màn hình + prompt qua Gemini Live WebSocket, in text + latency."""
import argparse
import base64
import json
import os
import sys
import time

import websockets


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("frame", help="path to a 480x270 JPEG frame")
    parser.add_argument("--model", default="gemini-2.5-flash-live-preview")
    parser.add_argument("--key", default=os.environ.get("GEMINI_KEY", ""))
    args = parser.parse_args()
    if not args.key:
        print("Missing key: pass --key or set GEMINI_KEY")
        sys.exit(1)
    url = (
        "wss://generativelanguage.googleapis.com/ws/"
        "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"
        f"?key={args.key}"
    )
    jpeg = base64.b64encode(open(args.frame, "rb").read()).decode()
    setup = {
        "setup": {
            "model": f"models/{args.model}",
            "generationConfig": {"candidateCount": 1, "responseModalities": ["TEXT"], "temperature": 0.1, "maxOutputTokens": 512},
            "systemInstruction": {"parts": [{"text": "Return only JSON with keys scene, event, confidence, novelty, comment_worthiness, visible_text."}]},
        }
    }
    turn = {
        "clientContent": {
            "turns": [{"role": "user", "parts": [
                {"inlineData": {"mimeType": "image/jpeg", "data": jpeg}},
                {"text": "Describe this screen."},
            ]}]
        }
    }
    started = time.monotonic()
    async def run() -> None:
        async with websockets.connect(url, max_size=20 * 1024 * 1024) as ws:
            await ws.send(json.dumps(setup))
            while True:
                message = json.loads(await ws.recv())
                if "setupComplete" in message:
                    break
            await ws.send(json.dumps(turn))
            text = ""
            while True:
                message = json.loads(await ws.recv())
                if "error" in message:
                    print("ERROR:", message["error"])
                    sys.exit(2)
                content = message.get("serverContent", {})
                for part in content.get("modelTurn", {}).get("parts", []):
                    text += part.get("text", "")
                if content.get("turnComplete") or content.get("interrupted"):
                    break
        print(f"latency_ms={(time.monotonic() - started) * 1000:.0f}")
        print(text[:1500])

    import asyncio
    asyncio.run(run())


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Cài dependency probe (nếu thiếu)**

Run: `.venv\Scripts\python.exe -m pip install websockets` (workdir `D:\LLM`) — chỉ nếu import thất bại. Kiểm tra trước: `.venv\Scripts\python.exe -c "import websockets"`.

- [ ] **Step 3: Chụp frame mẫu + chạy probe với `gemini-2.5-flash-live-preview`**

Run: `.venv\Scripts\python.exe scripts\probe_gemini_vision.py <frame.jpg> --model gemini-2.5-flash-live-preview` (key qua env `GEMINI_KEY`)
Expected: in `latency_ms` + JSON hợp lệ đủ 6 key. Nếu lỗi `1011 Internal` với TEXT modality → thử `--model gemini-2.0-flash-live-001`. Model thắng → cập nhật `GEMINI_LIVE_MODEL` trong `gemini_vision_runtime.rs` + commit (message: `fix(observation): pin working gemini live model`).

- [ ] **Step 4: Chạy toàn bộ test sau khi chốt model**

Run: `cargo test && cargo clippy --all-targets` (workdir `D:\LLM\gui-tauri\src-tauri`); `npx vitest run` (workdir `D:\LLM\gui-tauri`); `.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q` (workdir `D:\LLM`)

- [ ] **Step 5: End-to-end với app (cùng chủ nhân)**

1. Chủ nhân nhập Gemini key trong **Settings → Gemini Vision API Key → Apply Settings**.
2. Bật observation (frame chuyển động) → dòng detail phải hiện `vision gemini/...`.
3. Chờ reaction: `đã phát > 0`, chủ nhân nghe giọng đầy đủ.
4. Xác nhận llama-server vision KHÔNG spawn (tasklist không có `llama-server.exe`).
5. Chủ nhân thử key sai → sau 3 lỗi liên tiếp dòng detail đổi `vision local/...` và observation vẫn chạy.

- [ ] **Step 6: Cập nhật docs + commit cuối**

`docs/DEEPSEEK_SCREEN_AWARENESS_HANDOFF.md` — thêm entry 16 vào "What was diagnosed and fixed": mô tả Gemini Live vision (engine select, fallback, model đã chốt, kết quả live).
```bash
git add docs/DEEPSEEK_SCREEN_AWARENESS_HANDOFF.md
git commit -m "docs(observation): record gemini live vision integration results"
```

## Self-Review notes

- Spec coverage: key storage (Task 1), endpoint vision-config (Task 1), runtime WebSocket (Task 2), engine select + fallback + metrics (Task 3), Settings UI (Task 4), probe + e2e (Task 5) — đủ mọi mục trong spec.
- Deviation từ spec: `tungstenite` blocking thay `tokio-tungstenite` + `futures-util` (crate không có tokio runtime; blocking client khớp kiến trúc `reqwest::blocking` hiện có).
- `VisionError` mất derive `Copy` (thêm `Gemini(String)`); mọi usage hiện tại là pattern-match nên không phá code.
- Fallback đếm qua biến cục bộ `gemini_failures` trong `spawn_vision_worker` — không arm cooldown reaction (chỉ đổi engine).
- Fallback reset `restart_count = 0` để local được phép khởi động lại từ đầu (nếu không, restart_count = 2 sẽ chặn local trước khi nó được thử).
