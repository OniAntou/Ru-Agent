# Gemini Live Vision cho Screen Awareness — Design

- **Ngày:** 2026-08-02
- **Trạng thái:** Approved (chủ nhân duyệt từng phần)
- **Repo:** `D:\LLM` + `D:\LLM\gui-tauri`

## Mục tiêu

Thêm một vision engine thứ hai cho screen awareness: **Gemini Live API qua WebSocket
(BidiGenerateContent)**, dùng API key AI Studio. Engine tự chọn:

- Có Gemini key trong keyring → dùng Gemini Live (không spawn llama-server vision,
  tiết kiệm ~2–3 GB VRAM)
- Không có key → local Qwen3-VL như hiện tại
- Gemini lỗi liên tục (≥3 lần liên tiếp) → fallback vĩnh viễn về local Qwen trong
  phiên observation đó

## Kiến trúc

```
[frame capture 480x270] → [candidate đạt ngưỡng] → analyze(frames)
                                                       │
                    ┌──────────────────────────────────┴───────────────┐
                    │  spawn_vision_worker chọn engine                 │
                    │  (observation_runtime.rs:497)                    │
                    │  • GET /observation/vision-config (bridge)       │
                    │    → {engine: "gemini"|"local", api_key}         │
                    │  • engine=gemini → GeminiLiveVisionRuntime       │
                    │  • engine=local → LlamaVisionRuntime (như cũ)    │
                    └──────────────────────────────────────────────────┘
```

`GeminiLiveVisionRuntime` implement trait `VisionAnalyzer` (vision_runtime.rs:167-170):

- **start(api_key, is_cancelled)**: connect WebSocket
  `wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={KEY}`
  → gửi `BidiGenerateContentSetup`:
  - `model`: hằng số `GEMINI_LIVE_MODEL` — mặc định `gemini-2.5-flash-live-preview`
    (cần live-probe trước khi chốt; `gemini-3.1-flash-live-preview` có thể lỗi 1011
    với TEXT modality theo issue #2238)
  - `generationConfig`: `responseModalities: ["TEXT"]`, `temperature: 0.1`,
    `maxOutputTokens: 512`, `candidateCount: 1`
  - `systemInstruction`: giữ nguyên nội dung prompt Qwen hiện tại (giới hạn nội dung,
    giọng `anh`/`em`, schema JSON chặt)
  - chờ `setupComplete` (timeout 10s)
- **analyze(frames)**: gửi `BidiGenerateContentClientContent`:
  `{clientContent: {turns: [{role: "user", parts: [inlineData(image/jpeg, base64) x1-4, text prompt]}]}}`
  → drain `BidiGenerateContentServerContent`, gộp `modelTurn.parts[].text` tới
  `turnComplete: true` hoặc `interrupted: true` → parse JSON + validate như
  `parse_completion`/`validate_observation` hiện tại
- **stop()**: đóng WS (Drop cũng tự stop)
- Timeout mỗi turn 10s (giống `HTTP_TIMEOUT` hiện tại); WS rớt giữa chừng → tự
  reconnect tối đa 2 lần rồi mới tính lỗi

## Components

### Backend Python (`src/mini_gpt/server.py`, `credential_store.py`)

- `SettingsPayload` thêm field `gemini_vision_api_key` (max 4096) → lưu keyring
  `CredentialStore("mini_gpt", "gemini_vision_api_key")`, không vào settings.json
  (giống `api_key` hiện tại)
- Endpoint mới `GET /observation/vision-config` (auth token): trả
  `{engine: "gemini" | "local", api_key: str | null}` — có key thì engine=gemini
- `GET /settings` không đổi (không trả key)

### Settings UI (`gui-tauri/src/components/SettingsTab.tsx`)

- Input password "Gemini Vision API Key" + nút Save → `POST /settings {gemini_vision_api_key}`
- Sau save: clear input, không GET lại key (theo pattern api_key hiện tại)

### Rust (`gui-tauri/src-tauri/src/`)

- File mới `gemini_vision_runtime.rs`: `GeminiLiveVisionRuntime` + builder/parser
  thuần hàm cho unit test
- `observation_runtime.rs:497`: spawn_vision_worker gọi bridge vision-config;
  engine=gemini → spawn Gemini runtime (KHÔNG spawn llama-server, không cần port
  18111, không cần Job Object child — chỉ có socket); engine=local → như cũ
- Metrics thêm `vision_engine: "gemini" | "local"` (UI hiển thị)
- `VisionError` thêm variant `Gemini(String)` — mô tả lỗi không chứa key
- `Cargo.toml`: thêm `tokio-tungstenite`, `futures-util`, `base64`

## Xử lý lỗi & fallback

- Lỗi Gemini (connect/setup/quota/key/WS rớt sau reconnect/timeout turn) →
  `VisionError::Gemini`, log `eprintln` không chứa key
- Đếm lỗi **liên tiếp** ≥ 3 → đóng WS, `LlamaVisionRuntime::start_cancellable`
  như cũ (spawn llama-server 18111), log `vision: gemini failed Nx, falling back to local`
- Fallback vĩnh viễn trong phiên observation (không nhảy qua lại), reset khi
  observation tắt/bật lại
- Lỗi Gemini không arm cooldown reaction (giống cloud failures hiện tại)
- UI metrics hiển thị `vision gemini/local` rõ ràng

## Testing & Verification

1. **Unit tests (Rust)**: build setup JSON đúng schema; build clientContent turn
   (frame JPEG → inlineData base64); parse serverContent stream (gộp text, dừng
   đúng turnComplete/interrupted); parse → VisionObservation + validate; bộ đếm
   fallback 3 lỗi
2. **Live probe** `probe_gemini_vision.py`: 1 frame màn hình thật + prompt → xác
   nhận model TEXT modality (`gemini-2.5-flash-live-preview` trước, thử thêm
   `gemini-3.1-flash-live-preview`), latency, JSON schema
3. **Integration (key thật)**: observation → metrics `vision gemini` → `đã phát > 0`,
   user nghe đầy đủ; llama-server vision không spawn (giải phóng VRAM)
4. **Fallback test**: key sai → 3 lỗi → tự chuyển `vision local`, observation sống
5. **Verification đầy đủ**: pytest (test vision-config endpoint), vitest (SettingsTab),
   cargo test, clippy/fmt

## File ảnh hưởng

| File | Thay đổi |
|---|---|
| `src/mini_gpt/server.py` | SettingsPayload + vision-config endpoint |
| `src/mini_gpt/credential_store.py` | (tái dùng, có thể không sửa) |
| `gui-tauri/src-tauri/src/gemini_vision_runtime.rs` | mới |
| `gui-tauri/src-tauri/src/vision_runtime.rs` | VisionError::Gemini |
| `gui-tauri/src-tauri/src/observation_runtime.rs` | chọn engine + fallback + metrics |
| `gui-tauri/src-tauri/src/lib.rs` | (nếu cần bridge vision-config) |
| `gui-tauri/src-tauri/Cargo.toml` | deps mới |
| `gui-tauri/src/components/SettingsTab.tsx` | input key |
| `gui-tauri/src/components/ObservationControl.tsx` | hiển thị vision_engine |
| `tests/test_server_api.py` | test vision-config |
| `gui-tauri/src-tauri/src/gemini_vision_runtime.test.rs` | mới |

## Out of scope

- Ephemeral token (BidiGenerateContentConstrained) — dùng API key trực tiếp
- Vertex AI auth — chỉ AI Studio key
- Audio/video streaming realtime — chỉ TEXT output cho phân tích màn hình
- Session resumption (setup lại từ đầu khi reconnect)

## Live Probe Results (2026-08-02)

**Probe script:** `scripts/probe_gemini_vision.py`  
**API key:** AI Studio key (provided by user)  
**Date:** 2026-08-02

### Available models (50 via `models.list`)

Key models available with their supported methods:

| Model | Supported Methods |
|-------|-------------------|
| `gemini-2.5-flash` | generateContent, countTokens, createCachedContent, batchGenerateContent |
| `gemini-2.5-pro` | generateContent, countTokens, createCachedContent, batchGenerateContent |
| `gemini-2.0-flash` | generateContent, countTokens, createCachedContent, batchGenerateContent |
| `gemini-2.0-flash-001` | generateContent, countTokens, createCachedContent, batchGenerateContent |
| `gemini-2.0-flash-lite-001` | generateContent, countTokens, createCachedContent, batchGenerateContent |
| ... (44 more models) | ... |

### Critical Finding

**No models support `bidiGenerateContent` (Live API)** with this AI Studio API key.

The Live API (BidiGenerateContent WebSocket streaming) requires:
- Vertex AI project with the API enabled, OR
- Special preview access program

AI Studio keys only provide access to the REST `generateContent` API.

### Implications for Implementation

1. **Default model** in Rust code remains `gemini-2.5-flash-live-preview` (per plan)
2. **Runtime behavior**: `GeminiLiveVisionRuntime.connect()` will fail with close code 1008 (model not found/not supported for bidiGenerateContent)
3. **Fallback**: After 3 consecutive failures, `spawn_vision_worker` will automatically fall back to local Qwen3-VL
4. **User experience**: If user provides AI Studio key, they will see brief Gemini attempts (logged) then seamless fallback to local — observation continues working
5. **To enable Live API**: User needs Vertex AI project with `generativelanguage.googleapis.com` (BidiGenerateContent) enabled, or wait for public Live API access

### Probe Exit Codes

- `0`: Live API connection successful, model responded
- `1`: Live API connection failed (network/auth/other)
- `2`: API key valid but no Live API access (no models support bidiGenerateContent)

**Result:** Exit code 2 — AI Studio key has no Live API access.

---

*Entry 16 — Probe completed 2026-08-02*
