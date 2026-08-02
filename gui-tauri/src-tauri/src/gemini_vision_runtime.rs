use crate::frame_buffer::PreparedFrame;
use crate::vision_runtime::{
    validate_observation, VisionAnalyzer, VisionError, VisionObservation, VISION_PROMPT,
};
use base64::Engine;
use serde_json::{json, Value};
use std::io;
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};
use tungstenite::client_tls;
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Error as WsError, Message, WebSocket};

const GEMINI_LIVE_MODEL: &str = "gemini-2.5-flash-live-preview";
const GEMINI_WS_HOST: &str = "generativelanguage.googleapis.com:443";
const GEMINI_WS_PATH: &str =
    "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
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
    parts.push(json!({"text": format!("{VISION_PROMPT}{GEMINI_SCHEMA_PROMPT}")}));
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
    if let Some(parts) = content
        .get("modelTurn")
        .and_then(|turn| turn.get("parts"))
        .and_then(Value::as_array)
    {
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
}

impl GeminiLiveVisionRuntime {
    pub fn connect(api_key: &str, is_cancelled: &dyn Fn() -> bool) -> Result<Self, VisionError> {
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
        let (socket, _) = client_tls(url.as_str(), stream)
            .map_err(|error| VisionError::Gemini(format!("connect: {error}")))?;
        let mut runtime = Self { socket };
        runtime.send_setup(is_cancelled)?;
        Ok(runtime)
    }

    fn send_setup(&mut self, is_cancelled: &dyn Fn() -> bool) -> Result<(), VisionError> {
        let setup = build_setup_message(GEMINI_LIVE_MODEL);
        self.socket
            .send(Message::Text(setup.to_string().into()))
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

    fn read_message(
        &mut self,
        is_cancelled: &dyn Fn() -> bool,
    ) -> Result<Option<ServerEvent>, VisionError> {
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
            Ok(Message::Close(frame)) => {
                let code = frame
                    .map(|close| close.code.to_string())
                    .unwrap_or_else(|| "no code".to_owned());
                Err(VisionError::Gemini(format!("closed by server: {code}")))
            }
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
            .send(Message::Text(message.to_string().into()))
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
                    let observation: VisionObservation =
                        serde_json::from_str(&text).map_err(|_| VisionError::InvalidSchema)?;
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
        assert_eq!(
            setup["setup"]["model"],
            format!("models/{GEMINI_LIVE_MODEL}")
        );
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
        assert!(matches!(
            parse_server_message(&complete),
            Some(ServerEvent::TurnComplete)
        ));
        let interrupted = serde_json::json!({"serverContent": {"interrupted": true}});
        assert!(matches!(
            parse_server_message(&interrupted),
            Some(ServerEvent::Interrupted)
        ));
        let setup = serde_json::json!({"setupComplete": {}});
        assert!(matches!(
            parse_server_message(&setup),
            Some(ServerEvent::SetupComplete)
        ));
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
