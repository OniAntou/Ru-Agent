# VTS Motion FSM & Control Panel — Design

- **Date:** 2026-08-05
- **Status:** Approved (design review complete)
- **Scope:** VTube Studio control system — motion engine (FSM), context-aware scheduling, emotion-driven micro-actions, GUI intensity sliders, connection reliability
- **Out of scope:** dynamic catalog test UI, real audio reactivity, switching LLM_* custom params to standard VTS params

## 1. Goals

1. Make avatar motion look less robotic: add breathing, natural transitions, state-driven amplitudes.
2. Micro-actions/poses must respect context instead of random timers: never fire mid-speech, never clash with semantic actions, pose only at natural transition points.
3. Let the user tune motion intensity from the GUI (sliders persisted in settings, applied live).
4. Improve connection reliability: reconnect backoff never permanently gives up; keep-alive so VTS does not drop an idle socket.

## 2. Architecture

```
GUI (VoiceView sliders / ChatView typing)
        │  /vts/config, /vts/activity, /vts/status (poll 2–3s)
        ▼
routers/vts.py ──► _server_shared (VTSManager singleton, MotionConfig store)
                          │
        chat.py / observation.py ──► note_generating(True/False), set_emotion(EmotionTag)
        voice lifecycle          ──► set_speaking(True/False)
                          ▼
              vts_manager.VTSManager
                ├── MotionContextProvider (thread-safe inputs)
                ├── MotionStateMachine (FSM, runs on sway loop)
                └── controllers (Sway/Breath/Eyes/Head/Micro/Pose)
                          │ 10 Hz batch params
                          ▼
                    pyvts websocket
```

All motion logic runs on the existing VTS asyncio sway loop; no new threads or timers.

## 3. State machine

### 3.1 States

| State | Meaning | Motion policy |
|---|---|---|
| `IDLE` | Ru silent, not generating, no user activity | sway light + breath, sparse micro-actions, no poses |
| `LISTENING` | user typing / speaking | eyes lean toward "attention", subtle head nod, micro-actions minimized |
| `THINKING` | LLM generating | slight head tilt, distant gaze (eye drift), near-still |
| `SPEAKING` | TTS playing | stronger sway, head nod with speech, denser micro-actions |
| `POST_REPLY` | just finished speaking (2–5 s) | natural transition point: one pose if emotion fits, deep breath, then back |

### 3.2 Transitions

- `IDLE ⇄ LISTENING` — user typing signal, with hysteresis (LISTENING exits only after ~3 s without input; `LISTENING → IDLE` after 5–10 s no activity)
- `LISTENING|IDLE → THINKING` — generation start
- `THINKING → SPEAKING` — TTS start
- `SPEAKING → POST_REPLY` — TTS end
- `POST_REPLY → IDLE | LISTENING` — timeout 2–5 s
- Any state → `IDLE` when system idle long / app focus lost

### 3.3 Hysteresis

Every transition has a minimum dwell time (e.g. LISTENING needs 3 s of continuous input before re-entering IDLE; THINKING cannot flip to LISTENING). Prevents state flapping. All timing via `time.monotonic()`.

## 4. Components

### 4.1 `src/mini_gpt/vts_state.py` (new)

- `VtsMotionState` enum (`IDLE`, `LISTENING`, `THINKING`, `SPEAKING`, `POST_REPLY`)
- `MotionStateMachine`:
  - holds current state + entry timestamp
  - `update(context_snapshot, now) -> state` — evaluates transitions every tick
  - `on_enter(state)` hooks called once per transition (e.g. POST_REPLY entry → allow one pose)
  - `state_elapsed(now)`
  - pure logic, no VTS dependency → fully unit-testable

### 4.2 `src/mini_gpt/vts_context.py` (new)

- `MotionContextProvider` (thread-safe, locks):
  - setters: `set_speaking(bool)`, `set_generating(bool)`, `set_user_typing(bool)`, `set_emotion(EmotionTag)`, `set_system_idle(bool)`
  - `snapshot() -> MotionContextSnapshot` (immutable) consumed by sway loop each tick

### 4.3 `src/mini_gpt/vts_emotion.py` (new)

- `EmotionTag` enum: `none, joy, sad, angry, surprised, shy`
- `extract_reply_emotion(reply) -> tuple[str, EmotionTag]`:
  - prefers LLM tag `[[EMO:joy|sad|angry|surprised|shy|none]]` (stripped from reply before persistence)
  - falls back to normalized Vietnamese keyword heuristic (vui/mừng → joy; buồn → sad; giận → angry; ngạc nhiên → surprised; xấu hổ/ngại → shy)
  - malformed tag → heuristic only; default `none`

### 4.4 `src/mini_gpt/vts_manager.py` (extend)

- owns FSM + context provider; sway loop consumes snapshot, drives controllers per state
- `apply_config(MotionConfig)` live, survives disconnect
- reliability:
  - reconnect = infinite backoff (grow to 30 s cap, reset on success) — replaces `RECONNECT_MAX_ATTEMPTS = 5` give-up
  - keep-alive ping on the socket when injection is paused so VTS does not close it

### 4.5 `src/mini_gpt/vts_motion.py` (extend)

- `MotionConfig` dataclass with validated ranges:
  - sway amplitude idle (5–30), sway amplitude speaking (5–40)
  - head scale (0–100 %), eye range (0–100 %)
  - micro-action delay idle/speaking (1–120 s)
  - pose interval (10–600 s)
  - breath amplitude (0–5), breath rate (0.05–0.5 Hz)
  - per-state amplitude multipliers
- `BreathController` — slow ±small oscillation added to body sway; no new VTS param needed
- `MicroActionScheduler` / `PoseCycleScheduler` — take state + config; eligibility policy per state; pose fires once on POST_REPLY entry (transition point), never as a random timer during SPEAKING; micro-actions suppressed in LISTENING/THINKING
- pose–emotion rule: a POST_REPLY pose is allowed only when emotion is `joy` or `surprised`; `sad`/`angry`/`shy`/`none` never fire a pose (deep breath only)

## 5. API & plumbing

### 5.1 `routers/vts.py` (extend)

- `GET /vts/config` → current `MotionConfig` (defaults if unset)
- `POST /vts/config` → validate (422 + message on invalid, keep running config), persist to settings.json `vts_motion` section, apply live
- `POST /vts/activity` `{typing: bool}` — GUI pushes typing state (debounced client-side)
- `GET /vts/status` → `{status, connected, state, model_name, config}` (GUI polls 2–3 s)

### 5.2 Server plumbing (`_server_shared.py`)

- chat flow: `note_generating(True)` at generation start; `note_generating(False)` + `set_emotion(extract_reply_emotion(...))` at completion — wired into `chat.py` and `observation.py`
- voice lifecycle: TTS start/stop → `set_speaking(True/False)`

### 5.3 GUI (`gui-winui3/Views/VoiceView.xaml(.cs)`, `ApiService.cs`, `ChatView.xaml.cs`, `MainWindow.xaml.cs`)

- VTS card: realtime connection status + current FSM state label (Idle / Lắng nghe / Đang nghĩ / Đang nói)
- 5 sliders: Sway, Đầu, Mắt, Tần suất micro-action, Tần suất pose — debounce 800 ms, save via `/vts/config`, applied live
  - slider mapping (0–100 %): Sway → sway amplitude idle/speaking lerp between min/max; Đầu → head scale %; Mắt → eye range %; micro-action slider → delay range lerp (100 % = densest = min delays, 0 % = sparsest = max delays); pose slider → pose interval lerp (100 % = most frequent = min interval)
- ChatView text box typing → event → `/vts/activity {typing:true/false}` (debounced 2 s)
- settings.json section `vts_motion`, independent of backend choice; loaded at server start with defaults

## 6. Error handling

- invalid config → 422 + clear message, keep running config
- malformed emotion tag → heuristic fallback, never blocks chat
- VTS disconnect mid-state → FSM resets to IDLE; reconnect restores config; new state on reconnect
- `/vts/activity` bursts → GUI debounce; server never blocks

## 7. Testing

- `tests/test_vts_state.py` (new): valid/invalid transitions, hysteresis dwell times, POST_REPLY timeout
- `tests/test_vts_emotion.py` (new): LLM tag parsing, Vietnamese heuristic, tag stripping, fallback
- `tests/test_vts_config.py` (new): defaults, boundary validation, persistence round-trip
- `tests/test_vts_motion.py` (extend): schedulers honor state + config, breath bounded
- existing `test_vts_catalog_actions.py`, `test_vts_motion.py` keep passing; full suite green before done

## 8. Deferred (out of scope)

- dynamic catalog browser/test buttons in GUI
- real audio reactivity (level-based instead of binary speaking)
- switching custom LLM_* params to standard VTS params
