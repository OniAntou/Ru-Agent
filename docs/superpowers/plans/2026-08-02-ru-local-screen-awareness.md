# Ru Local Screen Awareness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an explicitly enabled, RAM-only primary-monitor observer that uses local Qwen3-VL to recognize visual events and the configured OpenCode/Kilo cloud model plus the current Ru OmniVoice to deliver timely proactive social reactions.

**Architecture:** Tauri/Rust owns DXGI capture, a bounded frame ring, event gating, the dedicated multimodal llama-server child, and the observation state machine. FastAPI receives only structured local-vision text, applies a non-persistent reaction policy, and asks the selected hosted model for one dynamic Ru line; the always-mounted React controller plays it through the existing OmniVoice/VTS path. Every process remains under the existing Windows kill-on-close Job Object, and cloud loss pauses observation before local Llama fallback may claim the GPU.

**Tech Stack:** Windows DXGI Desktop Duplication, Rust 1.97, Tauri 2, `dxgi-capture-rs` 1.2.2, `image` 0.25.10, `reqwest` 0.13.4, React 19, TypeScript 5.8, Python 3.11+, FastAPI/Pydantic, llama.cpp b10142+, Qwen3-VL-2B-Instruct GGUF, OpenCode Zen/Kilo Gateway, OmniVoice.

> **Execution status (2026-08-02): Tasks 1–5 passed; Tasks 6–9 pending.** Both
> pinned artifacts pass size/SHA-256 validation. Five consecutive synthetic
> observations passed the strict parser at load 1,827 ms, inference p50 1,680
> ms, and p95/worst 1,921 ms. llama.cpp b10142's compatible wire schema omits
> unsupported numeric bounds and the 2,000-character event repetition while
> `parse_response` still enforces every original bound locally. The hosted
> reaction boundary now applies the in-memory cadence/cancellation policy,
> forwards unfiltered observation text through authenticated endpoints, uses
> the current OmniVoice/VTS contract, and passes 87 focused backend tests
> without writing screen content to chat sessions, memory, or learning data.
> The Rust capture foundation now bounds prepared frames to a 20-frame,
> ten-second RAM ring, uses in-memory JPEG/fingerprints only, and passed an
> interactive primary-monitor DXGI capture plus all unit, check, format, and
> Clippy gates. The managed Rust vision boundary pins every llama-server
> argument to loopback, validates model sizes, sends one-to-four in-memory data
> URLs, strictly parses observations, and owns child cleanup. Its HTTP/process
> tests pass, and a fresh five-run live Qwen gate measured load 2,947 ms,
> inference p50 784 ms, and p95/worst 1,062 ms with no surviving vision process.

## Global Constraints

- The approved design is `docs/superpowers/specs/2026-08-01-ru-local-screen-awareness-design.md`; if this plan conflicts with it, the design wins.
- Observe only the Windows primary monitor. Observation is off on every app start and starts only from an explicit visible toggle.
- Capture continuously but retain only a fixed five-to-ten-second representative-frame ring in RAM. Never create a screenshot, video, OCR, prompt, observation, or reaction file.
- Raw images go only to the loopback vision server. OpenCode/Kilo receives the unfiltered textual observation and may therefore receive exact visible text, names, and messages.
- Do not add sensitive-content detection, redaction, automatic privacy pauses, or cover-face behavior.
- Every emitted screen-aware reaction uses the currently selected Ru OmniVoice. Do not emit a text-only reaction and do not substitute another voice.
- Do not add system-audio capture, microphone changes, audio ducking, input automation, gameplay instructions, code help, debugging, or technical screen assistance.
- Generate every final reaction dynamically through the configured OpenCode/Kilo model. Do not add canned reaction strings.
- Target three to six distinct comments per active minute, median event-to-first-audio latency at or below 2.5 seconds, and honest p50/p95/worst measurements.
- Never persist screen observations into session history, structured memory, self-learning, approved examples, or content-bearing logs.
- Do not intentionally keep the 8B local text model and local VLM resident on the 6 GiB GPU together. Cloud loss pauses observation and releases vision first.
- Preserve the current `VoiceVolume` lip-sync, private `[[VTS:...]]`/`[[VOICE_PACE:...]]` boundaries, sentence playback lifecycle, and `anh`/`em` persona contract.
- A Qwen model, quantization, or co-residency failure is a hard gate: stop execution, record evidence, and revise the design/plan instead of silently choosing another model.

## Pinned model artifacts

Source: [Qwen/Qwen3-VL-2B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct-GGUF), Apache-2.0, immutable revision `52d6c8ffea26cc873ac5ad116f8631268d7eb503`.

| Local path under `models/vision/qwen3-vl-2b-instruct/` | Bytes | SHA-256 |
|---|---:|---|
| `Qwen3VL-2B-Instruct-Q4_K_M.gguf` | 1,107,409,952 | `089d75c52f4b7ffc56ba998ffc50aae89fcafc755f9e7208aacca281dca6c2ae` |
| `mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf` | 445,053,216 | `f9a68fabba69c3b81e153367b2c7521030b0fa8bb0de400c9599c8e6725f9c82` |

The app never downloads these files automatically. The explicit setup command in Task 1 validates size and hash before promotion from a `.partial` file.

## File structure

### Model tooling

- `scripts/vision_model_manifest.json`: immutable source revision, license, filenames, sizes, and hashes.
- `scripts/setup_local_vision.ps1`: explicit, hash-checked, resumable-safe model download; no app startup side effects.
- `src/mini_gpt/local_vision_probe.py`: pure llama-server command/payload/response helpers used by the smoke runner.
- `scripts/benchmark_local_vision.py`: load/inference/co-residency measurement without saving images or observations.
- `tests/test_local_vision_probe.py`: manifest, command, payload, and parsing tests.

### Python reaction boundary

- `src/mini_gpt/screen_reaction.py`: bounded observation types, cadence/repetition/staleness policy, in-memory cancellation state, and prompt formatting.
- `tests/test_screen_reaction.py`: pure policy and prompt tests.
- `src/mini_gpt/ru_style.py`: authoritative screen-reaction system contract.
- `src/mini_gpt/openai_runtime.py`: bounded timeout and cooperative cancellation for hosted SSE generation.
- `src/mini_gpt/headless_runner.py`: hosted-only `generate_screen_reaction` entry point.
- `src/mini_gpt/server.py`: authenticated preflight/react/cancel endpoints and GPU/voice integration.
- `tests/test_openai_runtime.py`, `tests/test_headless_runner.py`, `tests/test_server_api.py`: cancellation, hosted routing, non-persistence, and preflight tests.

### Rust observation runtime

- `gui-tauri/src-tauri/src/frame_buffer.rs`: prepared frame, RAM ring, fingerprint/change score, and adaptive batch gate.
- `gui-tauri/src-tauri/src/screen_capture.rs`: primary-monitor DXGI source and display-loss recovery boundary.
- `gui-tauri/src-tauri/src/vision_runtime.rs`: model asset validation, llama-server child, loopback health, multimodal JSON request, and typed observation parsing.
- `gui-tauri/src-tauri/src/backend_bridge.rs`: authenticated preflight/react/cancel calls containing text only.
- `gui-tauri/src-tauri/src/observation_runtime.rs`: capture/vision/reaction workers, state machine, voice reservation, metrics, restart bounds, and cleanup.
- `gui-tauri/src-tauri/src/lib.rs`: Tauri commands/events, single-instance initialization, shared Job Object assignment, and shutdown ordering.
- `gui-tauri/src-tauri/Cargo.toml`, `Cargo.lock`: pinned capture/image/HTTP/channel/single-instance dependencies.

### Frontend

- `gui-tauri/src/lib/observationClient.ts`: typed invoke/event wrapper.
- `gui-tauri/src/components/ObservationControl.tsx`: persistent state indicator, toggle, reaction listener, and mandatory OmniVoice playback.
- `gui-tauri/src/components/ObservationControl.test.tsx`: state, cleanup, failure, and playback tests.
- `gui-tauri/src/lib/playVoice.ts`, `playVoice.test.ts`: non-interrupting proactive playback and first-playing callback.
- `gui-tauri/src/App.tsx`, `App.css`: always-mounted control and accessible state styling.

### Active documentation

- `README.md`, `gui-tauri/README.md`, `TODO.md`, `docs/ANTIGRAVITY_HANDOFF.md`, and the approved design spec: implemented behavior, setup, gates, and measured status.

---

### Task 1: Pin model assets and create a no-content local vision benchmark

**Files:**
- Create: `scripts/vision_model_manifest.json`
- Create: `scripts/setup_local_vision.ps1`
- Create: `src/mini_gpt/local_vision_probe.py`
- Create: `scripts/benchmark_local_vision.py`
- Create: `tests/test_local_vision_probe.py`

**Interfaces:**
- Produces: `VisionProbeConfig`, `build_server_command(config) -> list[str]`, `build_request(images_b64) -> dict`, and `parse_response(payload) -> dict`.
- Produces: verified model files at the two exact paths in “Pinned model artifacts”.
- Consumes: `tools/llama.cpp-bin/llama-server.exe` b10142 or newer.

- [ ] **Step 1: Write the manifest and failing manifest/command tests**

Create the manifest with exact immutable values:

```json
{
  "schema_version": 1,
  "repository": "Qwen/Qwen3-VL-2B-Instruct-GGUF",
  "revision": "52d6c8ffea26cc873ac5ad116f8631268d7eb503",
  "license": "apache-2.0",
  "files": [
    {
      "name": "Qwen3VL-2B-Instruct-Q4_K_M.gguf",
      "size": 1107409952,
      "sha256": "089d75c52f4b7ffc56ba998ffc50aae89fcafc755f9e7208aacca281dca6c2ae"
    },
    {
      "name": "mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf",
      "size": 445053216,
      "sha256": "f9a68fabba69c3b81e153367b2c7521030b0fa8bb0de400c9599c8e6725f9c82"
    }
  ]
}
```

Add tests that require fixed local paths and supported llama-server arguments:

```python
def test_manifest_is_revision_and_hash_pinned() -> None:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    assert manifest["revision"] == "52d6c8ffea26cc873ac5ad116f8631268d7eb503"
    assert manifest["license"] == "apache-2.0"
    assert {item["size"] for item in manifest["files"]} == {1107409952, 445053216}
    assert all(re.fullmatch(r"[0-9a-f]{64}", item["sha256"]) for item in manifest["files"])


def test_server_command_is_loopback_only_and_multimodal(tmp_path: Path) -> None:
    config = VisionProbeConfig(project_root=tmp_path, port=18111)
    command = build_server_command(config)
    assert command[0].endswith("llama-server.exe")
    assert command[command.index("--host") + 1] == "127.0.0.1"
    assert command[command.index("--port") + 1] == "18111"
    assert "--mmproj" in command
    assert command[command.index("--ctx-size") + 1] == "4096"
    assert command[command.index("--image-max-tokens") + 1] == "512"
    assert "--no-webui" in command
```

- [ ] **Step 2: Run the focused tests and confirm the helper is missing**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_local_vision_probe.py -q
```

Expected: collection/import failure for `mini_gpt.local_vision_probe`.

- [ ] **Step 3: Implement the pure probe contract and strict response parser**

Use this public shape:

```python
@dataclass(frozen=True, slots=True)
class VisionProbeConfig:
    project_root: Path
    port: int

    @property
    def model_dir(self) -> Path:
        return self.project_root / "models" / "vision" / "qwen3-vl-2b-instruct"


def build_server_command(config: VisionProbeConfig) -> list[str]:
    return [
        str(config.project_root / "tools" / "llama.cpp-bin" / "llama-server.exe"),
        "--model", str(config.model_dir / "Qwen3VL-2B-Instruct-Q4_K_M.gguf"),
        "--mmproj", str(config.model_dir / "mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf"),
        "--host", "127.0.0.1", "--port", str(config.port),
        "--ctx-size", "4096", "--n-predict", "192", "--parallel", "1",
        "--n-gpu-layers", "99", "--flash-attn", "on",
        "--cache-type-k", "q8_0", "--cache-type-v", "q8_0",
        "--image-max-tokens", "512", "--reasoning", "off", "--no-webui",
    ]


def build_request(images_b64: Sequence[str]) -> dict:
    if not 1 <= len(images_b64) <= 4:
        raise ValueError("vision probe requires one to four images")
    content = [{"type": "text", "text": VISION_OBSERVATION_PROMPT}]
    content.extend(
        {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{value}"}}
        for value in images_b64
    )
    return {
        "model": "qwen3-vl-2b-instruct",
        "messages": [{"role": "user", "content": content}],
        "temperature": 0.1,
        "max_tokens": 192,
        "stream": False,
        "response_format": VISION_JSON_SCHEMA,
    }


def parse_response(payload: dict) -> dict:
    content = payload["choices"][0]["message"]["content"]
    decoded = json.loads(content)
    required = {"scene", "event", "confidence", "novelty", "comment_worthiness", "visible_text"}
    if set(decoded) != required:
        raise ValueError("vision response does not match the observation schema")
    return decoded
```

The combined wire-schema/parser contract must bound `scene` to 500 characters,
`event` to 2,000 characters, `visible_text` to 16 strings of 200 characters,
and all three scores to `[0, 1]`. For llama.cpp b10142, the wire schema omits
numeric min/max and `event.maxLength=2000` because that build cannot compile
them; `parse_response` must enforce those omitted bounds before returning.

- [ ] **Step 4: Implement the explicit hash-checked PowerShell downloader**

The script must require consent and use only paths resolved beneath `D:\LLM\models\vision\qwen3-vl-2b-instruct`:

```powershell
param([switch]$ConfirmModelDownload)
$ErrorActionPreference = "Stop"
if (-not $ConfirmModelDownload) {
    throw "Pass -ConfirmModelDownload to download the pinned 1.55 GB Qwen vision assets."
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot "vision_model_manifest.json") -Raw | ConvertFrom-Json
$modelDir = Join-Path $repoRoot "models\vision\qwen3-vl-2b-instruct"
New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
foreach ($file in $manifest.files) {
    $target = Join-Path $modelDir $file.name
    $partial = "$target.partial"
    if (Test-Path -LiteralPath $target) {
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Get-Item -LiteralPath $target).Length -eq $file.size -and $hash -eq $file.sha256) { continue }
        throw "Existing model artifact failed validation: $target"
    }
    $url = "https://huggingface.co/$($manifest.repository)/resolve/$($manifest.revision)/$($file.name)?download=true"
    Invoke-WebRequest -Uri $url -OutFile $partial
    $item = Get-Item -LiteralPath $partial
    $hash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne $file.size -or $hash -ne $file.sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw "Downloaded artifact failed size/hash validation: $($file.name)"
    }
    Move-Item -LiteralPath $partial -Destination $target
}
```

- [ ] **Step 5: Implement and test the benchmark runner**

The runner must create an in-memory tiny JPEG smoke input, start the pinned server directly without a shell, wait on `/health`, run five warm requests, print the parsed observation only to the console, and write no report containing the observation. Numeric output uses this shape:

```json
{"load_ms": 0.0, "runs_ms": [0.0], "p50_ms": 0.0, "p95_ms": 0.0, "max_ms": 0.0, "gpu_before_mib": 0, "gpu_peak_mib": 0}
```

Test subprocess cleanup with a fake child and response parsing with a fake HTTP server, then run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_local_vision_probe.py -q
```

Expected: PASS; no model process is started by tests.

- [ ] **Step 6: Download and run the live model smoke gate**

```powershell
powershell -ExecutionPolicy Bypass -File D:\LLM\scripts\setup_local_vision.ps1 -ConfirmModelDownload
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\benchmark_local_vision.py --runs 5
```

Expected: both hashes match, all five responses satisfy the schema, the server exits in `finally`, and the output reports load/p50/p95/peak VRAM without writing image or observation content. Stop execution here if the server cannot load or parse Qwen3-VL.

- [ ] **Step 7: Commit the model tooling**

```powershell
git add scripts/vision_model_manifest.json scripts/setup_local_vision.ps1 scripts/benchmark_local_vision.py src/mini_gpt/local_vision_probe.py tests/test_local_vision_probe.py
git commit -m "build: add pinned local vision probe"
```

Do not stage `models/`, `.partial` files, caches, or numeric live reports.

---

### Task 2: Implement the pure screen-observation and reaction policy

**Files:**
- Create: `src/mini_gpt/screen_reaction.py`
- Create: `tests/test_screen_reaction.py`
- Modify: `src/mini_gpt/ru_style.py`
- Modify: `tests/test_ru_style.py`

**Interfaces:**
- Produces: `VisionObservation`, `ReactionRequest`, `ReactionDecision`, `ReactionCoordinator`, `format_observation_prompt`, and `RU_SCREEN_REACTION_SYSTEM_PROMPT`.
- Consumes: no server globals, filesystem, memory store, or network runtime.

- [ ] **Step 1: Write failing policy tests for staleness, cadence, repetition, cancellation, and unfiltered text**

```python
def observation(identifier: str = "obs-1", *, age_ms: int = 100, event: str = "anh vừa hụt ba phát") -> ReactionRequest:
    return ReactionRequest(
        observation_id=identifier,
        event_age_ms=age_ms,
        observation=VisionObservation(
            scene="game bắn súng",
            event=event,
            confidence=0.9,
            novelty=0.9,
            comment_worthiness=0.9,
            visible_text=("PRIVATE MESSAGE: mật khẩu là abc",),
        ),
    )


def test_policy_allows_distinct_high_value_event_and_preserves_visible_text() -> None:
    coordinator = ReactionCoordinator(clock=iter((100.0,)).__next__)
    decision = coordinator.begin(observation())
    assert decision.allowed is True
    assert "PRIVATE MESSAGE: mật khẩu là abc" in format_observation_prompt(observation().observation, ())


def test_policy_rejects_stale_repeated_and_too_frequent_events() -> None:
    now = {"value": 100.0}
    coordinator = ReactionCoordinator(clock=lambda: now["value"])
    assert coordinator.begin(observation("first")).allowed
    coordinator.complete("first", "Câu đầu")
    assert coordinator.begin(observation("stale", age_ms=4501)).reason == "stale"
    now["value"] = 101.0
    assert coordinator.begin(observation("repeat")).reason in {"repeat", "cooldown"}


def test_cancel_marks_only_current_pending_generation() -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    assert coordinator.begin(observation("current")).allowed
    coordinator.cancel("current")
    assert coordinator.is_cancelled("current") is True
    assert coordinator.is_cancelled("different") is False
```

- [ ] **Step 2: Run the policy/style tests and confirm missing symbols**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_screen_reaction.py tests\test_ru_style.py -q
```

Expected: import failures for the new module/prompt.

- [ ] **Step 3: Implement bounded immutable types and deterministic policy**

Use these constants and signatures:

```python
MAX_EVENT_AGE_MS = 4_500
MIN_REACTION_INTERVAL_SECONDS = 10.0
ROUTINE_REACTION_INTERVAL_SECONDS = 20.0
REPEAT_WINDOW_SECONDS = 30.0
MIN_CONFIDENCE = 0.55
MIN_NOVELTY = 0.55
MIN_COMMENT_WORTHINESS = 0.60


@dataclass(frozen=True, slots=True)
class VisionObservation:
    scene: str
    event: str
    confidence: float
    novelty: float
    comment_worthiness: float
    visible_text: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class ReactionRequest:
    observation_id: str
    event_age_ms: int
    observation: VisionObservation


@dataclass(frozen=True, slots=True)
class ReactionDecision:
    allowed: bool
    reason: str


class ReactionCoordinator:
    def __init__(self, *, clock=monotonic) -> None:
        self._clock = clock
        self._lock = threading.Lock()
        self._recent_events: deque[tuple[str, float]] = deque(maxlen=12)
        self._recent_reactions: deque[str] = deque(maxlen=6)
        self._last_completed_at: float | None = None
        self._pending_id: str | None = None
        self._cancelled_id: str | None = None

    def begin(self, request: ReactionRequest) -> ReactionDecision:
        if request.event_age_ms > MAX_EVENT_AGE_MS:
            return ReactionDecision(False, "stale")
        observation = request.observation
        if observation.confidence < MIN_CONFIDENCE:
            return ReactionDecision(False, "low_confidence")
        if observation.novelty < MIN_NOVELTY or observation.comment_worthiness < MIN_COMMENT_WORTHINESS:
            return ReactionDecision(False, "low_value")
        now = self._clock()
        key = " ".join(observation.event.casefold().split())
        with self._lock:
            while self._recent_events and now - self._recent_events[0][1] > REPEAT_WINDOW_SECONDS:
                self._recent_events.popleft()
            if any(previous == key for previous, _at in self._recent_events):
                return ReactionDecision(False, "repeat")
            if self._pending_id is not None:
                return ReactionDecision(False, "busy")
            elapsed = float("inf") if self._last_completed_at is None else now - self._last_completed_at
            if elapsed < MIN_REACTION_INTERVAL_SECONDS:
                return ReactionDecision(False, "cooldown")
            high_value = observation.comment_worthiness >= 0.75 or observation.novelty >= 0.80
            if not high_value and elapsed < ROUTINE_REACTION_INTERVAL_SECONDS:
                return ReactionDecision(False, "routine_cooldown")
            self._pending_id = request.observation_id
            self._cancelled_id = None
            self._recent_events.append((key, now))
            return ReactionDecision(True, "allowed")

    def cancel(self, observation_id: str) -> None:
        with self._lock:
            if self._pending_id == observation_id:
                self._cancelled_id = observation_id

    def cancel_pending(self) -> None:
        with self._lock:
            self._cancelled_id = self._pending_id

    def is_cancelled(self, observation_id: str) -> bool:
        with self._lock:
            return self._cancelled_id == observation_id

    def complete(self, observation_id: str, spoken_text: str) -> None:
        with self._lock:
            if self._pending_id != observation_id:
                return
            if self._cancelled_id != observation_id:
                self._last_completed_at = self._clock()
                self._recent_reactions.append(spoken_text.strip()[:500])
            self._pending_id = None
            self._cancelled_id = None

    def fail(self, observation_id: str) -> None:
        with self._lock:
            if self._pending_id == observation_id:
                self._pending_id = None
                self._cancelled_id = None

    def recent_reactions(self) -> tuple[str, ...]:
        with self._lock:
            return tuple(self._recent_reactions)
```

Keep only `deque(maxlen=12)` normalized event keys and `deque(maxlen=6)` spoken reactions. `begin` rejects age over 4,500 ms, low scores, a normalized duplicate within 30 seconds, any event within ten seconds of the previous completed reaction, and routine events until 20 seconds have elapsed. This bounds high-value speech at six starts per minute while allowing routine distinct events to reach three. It reserves one pending ID under a `threading.Lock`; `cancel_pending()` marks that ID cancelled without inventing a replacement. No method writes to disk or logs request content.

- [ ] **Step 4: Add the screen-only Ru prompt**

Append a constant that starts with `RU_CLOUD_SYSTEM_PROMPT` and explicitly requires one short spoken social reaction:

```python
RU_SCREEN_REACTION_SYSTEM_PROMPT = RU_CLOUD_SYSTEM_PROMPT + textwrap.dedent("""

    SCREEN REACTION MODE:
    The user did not ask a question. React socially to the supplied visual event in one short, natural line as Ru.
    Use Vietnamese anh/em address. Light teasing and mild profanity are allowed when playful and contextually justified.
    Do not explain code, diagnose software, give gameplay instructions, summarize the whole screen, mention screenshots/vision/models/prompts, or claim an action outside the event.
    Never use a prepared catchphrase. Do not repeat recent reactions supplied in the request.
    Return only the line Ru should say plus valid private VTS/VOICE_PACE metadata when the catalog permits it.
""")
```

`format_observation_prompt` serializes the full observation JSON without redaction, followed by the last six reactions, and caps the complete prompt at 6,000 characters by rejecting oversized input rather than silently persisting/truncating content.

- [ ] **Step 5: Run the focused suite**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_screen_reaction.py tests\test_ru_style.py -q
```

Expected: PASS.

- [ ] **Step 6: Commit the pure reaction policy**

```powershell
git add src/mini_gpt/screen_reaction.py src/mini_gpt/ru_style.py tests/test_screen_reaction.py tests/test_ru_style.py
git commit -m "feat: add screen reaction policy"
```

---

### Task 3: Add cancellable hosted generation and authenticated observation APIs

**Files:**
- Modify: `src/mini_gpt/openai_runtime.py`
- Modify: `src/mini_gpt/headless_runner.py`
- Modify: `src/mini_gpt/server.py`
- Modify: `tests/test_openai_runtime.py`
- Modify: `tests/test_headless_runner.py`
- Modify: `tests/test_server_api.py`

**Interfaces:**
- Consumes: `ReactionCoordinator`, `ReactionRequest`, `VisionObservation`, `format_observation_prompt`, `RU_SCREEN_REACTION_SYSTEM_PROMPT`.
- Produces: `OpenAIGenerationCancelled`, `ModelRunner.generate_screen_reaction(...)`, `POST /observation/preflight`, `POST /observation/react`, and `POST /observation/cancel`.
- Produces reaction response: `{"status":"ok","reaction":{"observation_id","text","voice_label","voice_speed","cloud_ms"}}` or `{"status":"skipped","reason":...}`.

- [ ] **Step 1: Write failing OpenAI runtime tests for bounded timeout and cancellation**

```python
def test_generate_passes_screen_timeout_and_stops_when_cancelled(monkeypatch) -> None:
    cancelled = {"value": False}

    class CancellingStream(FakeStreamResponse):
        def __iter__(self):
            yield b'data: {"choices":[{"delta":{"content":"Chao"}}]}\n'
            cancelled["value"] = True
            yield b'data: {"choices":[{"delta":{"content":" anh"}}]}\n'

    seen = {}
    def fake_open(_request, timeout):
        seen["timeout"] = timeout
        return CancellingStream([])

    monkeypatch.setattr(runtime_module, "urlopen", fake_open)
    with pytest.raises(OpenAIGenerationCancelled):
        OpenAIRuntime("key").generate(
            [("user", "event")], 64, 0.8, 40,
            timeout_seconds=4.0,
            max_retries=0,
            is_cancelled=lambda: cancelled["value"],
        )
    assert seen["timeout"] == 4.0
```

- [ ] **Step 2: Extend the hosted runtime without changing chat defaults**

Add `timeout_seconds: float = _STREAM_TIMEOUT_SECONDS`, `max_retries: int = _MAX_RETRIES`, and `is_cancelled: Callable[[], bool] | None = None` to `generate`; add the same timeout/cancellation inputs to `_read_stream`. Use this exact guard in both methods:

```python
class OpenAIGenerationCancelled(RuntimeError):
    pass


def _raise_if_cancelled(is_cancelled: Callable[[], bool] | None) -> None:
    if is_cancelled is not None and is_cancelled():
        raise OpenAIGenerationCancelled("Hosted generation was superseded.")


_raise_if_cancelled(is_cancelled)
with urlopen(request_factory(), timeout=timeout_seconds) as response:
    for raw_line in response:
        _raise_if_cancelled(is_cancelled)
        # Keep the existing SSE decode/accumulation logic unchanged here.

for attempt in range(max_retries + 1):
    _raise_if_cancelled(is_cancelled)
    # Keep the existing retry body, but pass timeout_seconds/is_cancelled to _read_stream.
    if attempt < max_retries:
        _raise_if_cancelled(is_cancelled)
        time.sleep(_RETRY_DELAY_SECONDS * (attempt + 1))
```

Check `is_cancelled()` before opening, before processing every SSE line, after the stream closes, and before any retry sleep. Pass `timeout_seconds` to `urlopen`; screen generation uses `4.0` and zero retries, while ordinary chat retains 120 seconds and two retries.

- [ ] **Step 3: Write failing hosted-only runner tests**

```python
def test_screen_reaction_uses_hosted_runtime_without_search_memory_or_history(monkeypatch) -> None:
    runner = ModelRunner()
    runner.backend = BACKEND_OPENCODE
    captured = {}
    runner.openai_runtime = SimpleNamespace(
        generate=lambda messages, *args, **kwargs: captured.update(messages=messages, kwargs=kwargs) or "Đau mắt giùm anh luôn."
    )
    reply = runner.generate_screen_reaction(
        "{\"event\":\"missed shots\"}",
        vts_instruction="",
        is_cancelled=lambda: False,
    )
    assert reply == "Đau mắt giùm anh luôn."
    assert captured["messages"][0][1].startswith(RU_SCREEN_REACTION_SYSTEM_PROMPT)
    assert captured["kwargs"]["timeout_seconds"] == 4.0
    assert captured["kwargs"]["max_retries"] == 0
```

- [ ] **Step 4: Implement `ModelRunner.generate_screen_reaction`**

```python
def generate_screen_reaction(
    self,
    observation_prompt: str,
    *,
    vts_instruction: str,
    is_cancelled,
    on_complete_timing=None,
) -> str:
    if self.backend not in (BACKEND_OPENCODE, BACKEND_KILO) or self.openai_runtime is None:
        raise RuntimeError("Screen reactions require an active OpenCode or Kilo cloud backend.")
    system = RU_SCREEN_REACTION_SYSTEM_PROMPT + VTS_ACTION_INSTRUCTION + VOICE_PACE_INSTRUCTION + vts_instruction
    return self.openai_runtime.generate(
        (("system", system), ("user", observation_prompt)),
        64, 0.85, 40,
        on_complete_timing=on_complete_timing,
        timeout_seconds=4.0,
        max_retries=0,
        is_cancelled=is_cancelled,
    )
```

- [ ] **Step 5: Write failing API tests for preflight, non-persistence, cancellation, and raw visible text**

Use a shared valid request fixture and make the content boundary explicit:

```python
@pytest.fixture
def valid_observation_payload() -> dict:
    return {
        "observation_id": "obs-1",
        "event_age_ms": 100,
        "observation": {
            "scene": "game",
            "event": "missed shots",
            "confidence": 0.9,
            "novelty": 0.9,
            "comment_worthiness": 0.9,
            "visible_text": ["private message abc"],
        },
    }


def test_observation_react_passes_unfiltered_visible_text_to_cloud(
    monkeypatch, valid_observation_payload
) -> None:
    captured = {}
    server.core.runner.backend = headless_runner.BACKEND_OPENCODE
    monkeypatch.setattr(server, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0))
    monkeypatch.setattr(
        server.core.runner,
        "generate_screen_reaction",
        lambda prompt, **kwargs: captured.update(prompt=prompt) or "Đau mắt giùm anh luôn.",
    )
    with TestClient(server.app) as client:
        response = client.post("/observation/react", json=valid_observation_payload)
    assert response.json()["status"] == "ok"
    assert "private message abc" in captured["prompt"]


def test_observation_react_does_not_write_session_memory_or_learning(
    monkeypatch, valid_observation_payload
) -> None:
    server.core.runner.backend = headless_runner.BACKEND_OPENCODE
    monkeypatch.setattr(server, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0))
    monkeypatch.setattr(server.core.runner, "generate_screen_reaction", lambda *args, **kwargs: "Một câu ngắn.")
    for owner, name in (
        (server.core, "remember"),
        (server, "_learn_from_turn"),
        (server, "_session_append_messages"),
    ):
        monkeypatch.setattr(owner, name, lambda *args, **kwargs: pytest.fail("screen reaction persisted"))
    with TestClient(server.app) as client:
        assert client.post("/observation/react", json=valid_observation_payload).json()["status"] == "ok"
```

Also add named tests `test_observation_preflight_requires_cloud_and_warms_current_omnivoice`, `test_observation_cancel_marks_pending_request`, and `test_chat_request_cancels_pending_screen_reaction_before_generation`. The first asserts local backend returns `status:error` while hosted backend calls `prepare_speech` with the saved OmniVoice selection; the second reserves `obs-1`, posts its ID, and asserts `is_cancelled("obs-1")`; the third spies on `cancel_pending()` before the existing websocket generation path runs.

- [ ] **Step 6: Add bounded Pydantic request models and endpoints**

```python
class VisionObservationPayload(BaseModel):
    scene: str = Field(max_length=500)
    event: str = Field(max_length=2_000)
    confidence: float = Field(ge=0.0, le=1.0)
    novelty: float = Field(ge=0.0, le=1.0)
    comment_worthiness: float = Field(ge=0.0, le=1.0)
    visible_text: list[str] = Field(default_factory=list, max_length=16)


class ObservationReactionPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")
    event_age_ms: int = Field(ge=0, le=60_000)
    observation: VisionObservationPayload


class ObservationCancelPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")
```

`/observation/preflight` verifies a hosted backend, calls `_gpu_lifecycle().prepare_speech(_voice_selection())`, and returns the effective `voice_label`/`voice_speed`. `/observation/react` applies the pure policy, acquires `_TURN_LOCK` and `_MEMORY_TURN_LOCK` non-blocking, obtains the current VTS catalog, generates one reaction, parses/strips private markers, triggers valid VTS codes, and returns cleaned text plus `cloud_ms = int(round(cloud_seconds * 1000))`. It never calls memory/session functions and never logs observation or reaction content. `/observation/cancel` only marks the matching pending generation.

Before normal chat tests `_TURN_LOCK`, call `_SCREEN_REACTIONS.cancel_pending()` so a direct user turn takes priority.

- [ ] **Step 7: Run backend focused tests**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py tests\test_headless_runner.py tests\test_screen_reaction.py tests\test_server_api.py -q
```

Expected: PASS; existing chat stream/retry semantics remain unchanged.

- [ ] **Step 8: Commit the hosted reaction boundary**

```powershell
git add src/mini_gpt/openai_runtime.py src/mini_gpt/headless_runner.py src/mini_gpt/server.py tests/test_openai_runtime.py tests/test_headless_runner.py tests/test_server_api.py
git commit -m "feat: add hosted screen reaction API"
```

---

### Task 4: Build the bounded Rust frame ring and primary-monitor DXGI source

**Files:**
- Modify: `gui-tauri/src-tauri/Cargo.toml`
- Modify: `gui-tauri/src-tauri/Cargo.lock`
- Create: `gui-tauri/src-tauri/src/frame_buffer.rs`
- Create: `gui-tauri/src-tauri/src/screen_capture.rs`
- Modify: `gui-tauri/src-tauri/src/lib.rs`

**Interfaces:**
- Produces: `CapturedFrame`, `PreparedFrame`, `FrameRing`, `AdaptiveGate`, `FrameSource`, `DxgiPrimaryFrameSource`.
- Produces constants: `SAMPLE_INTERVAL=500ms`, `RING_CAPACITY=20`, `MAX_FRAME_AGE=10s`, `MIN_ANALYSIS_INTERVAL=1500ms`, `MAX_ANALYSIS_INTERVAL=5000ms`, batch size 3, maximum prepared dimensions 960×540, JPEG quality 80.
- Consumes no Tauri/frontend/Python state.

- [ ] **Step 1: Pin Windows capture and image dependencies**

```toml
[dependencies]
base64 = "=0.22.1"
crossbeam-channel = "=0.5.16"
image = { version = "=0.25.10", default-features = false, features = ["jpeg"] }

[target.'cfg(windows)'.dependencies]
dxgi-capture-rs = "=1.2.2"
```

Keep the existing `windows-sys` Job Object dependency. Run `cargo update` only for packages required by these direct pins; inspect `Cargo.lock` before staging.

- [ ] **Step 2: Write failing frame-ring and adaptive-gate tests**

```rust
#[test]
fn ring_overwrites_oldest_and_clear_releases_every_frame() {
    let mut ring = FrameRing::new(3, Duration::from_secs(10));
    for sequence in 0..4 {
        ring.push(sample(sequence, sequence * 500));
    }
    assert_eq!(ring.sequences(), vec![1, 2, 3]);
    ring.clear();
    assert!(ring.is_empty());
}

#[test]
fn active_scene_batches_three_frames_but_static_scene_backs_off() {
    let mut gate = AdaptiveGate::new();
    assert_eq!(gate.observe(0, 0.90), GateDecision::Analyze);
    assert_eq!(gate.observe(500, 0.90), GateDecision::Wait);
    assert_eq!(gate.observe(1_500, 0.90), GateDecision::Analyze);
    assert_eq!(gate.observe(2_000, 0.01), GateDecision::Wait);
    assert_eq!(gate.observe(6_500, 0.01), GateDecision::Analyze);
}

#[test]
fn prepare_frame_never_accepts_invalid_bgra_length() {
    assert!(prepare_frame(&CapturedFrame { width: 10, height: 10, bgra: vec![0; 3], captured_at_unix_ms: 1 }).is_err());
}
```

- [ ] **Step 3: Implement immutable prepared frames and the bounded ring**

```rust
pub struct CapturedFrame {
    pub width: u32,
    pub height: u32,
    pub bgra: Vec<u8>,
    pub captured_at_unix_ms: u64,
}

#[derive(Clone)]
pub struct PreparedFrame {
    pub sequence: u64,
    pub captured_at_unix_ms: u64,
    pub jpeg: Arc<[u8]>,
    pub fingerprint: Arc<[u8]>,
}

pub struct FrameRing {
    frames: VecDeque<PreparedFrame>,
    capacity: usize,
    max_age: Duration,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GateDecision { Wait, Analyze }

pub struct AdaptiveGate {
    last_analysis_ms: Option<u64>,
}

impl FrameRing {
    pub fn new(capacity: usize, max_age: Duration) -> Self;
    pub fn push(&mut self, frame: PreparedFrame);
    pub fn recent_batch(&self, count: usize) -> Vec<PreparedFrame>;
    pub fn clear(&mut self);
}
```

Convert BGRA to RGB, preserve aspect ratio inside 960×540, encode JPEG quality 80 entirely into `Vec<u8>`, and derive a 64×36 luma fingerprint. `mean_absolute_difference` returns `[0,1]`. No function in this module accepts a filesystem path.

- [ ] **Step 4: Define the testable capture trait and Windows implementation**

```rust
pub trait FrameSource: Send {
    fn next_frame(&mut self, timeout: Duration) -> Result<Option<CapturedFrame>, CaptureFailure>;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureFailure {
    AccessDenied,
    AccessLost,
    NoPrimaryMonitor,
    Fatal,
}

#[cfg(windows)]
pub struct DxgiPrimaryFrameSource {
    manager: dxgi_capture_rs::DXGIManager,
}
```

Construct `DXGIManager::new(500)`, select source index `0` as the crate's documented primary-monitor source, use `capture_frame_with_metadata`, treat `Timeout` as `Ok(None)`, map `AccessLost` so the owner can recreate the source, and flatten `BGRA8` to a bounded `Vec<u8>` only after checked width×height×4 arithmetic.

- [ ] **Step 5: Add an ignored interactive capture test with no file output**

```rust
#[cfg(windows)]
#[test]
#[ignore = "requires an interactive Windows desktop"]
fn captures_primary_monitor_into_memory() {
    let mut source = DxgiPrimaryFrameSource::new().unwrap();
    let frame = source.next_frame(Duration::from_secs(2)).unwrap().unwrap();
    assert!(frame.width >= 640);
    assert!(frame.height >= 480);
    assert_eq!(frame.bgra.len(), frame.width as usize * frame.height as usize * 4);
}
```

- [ ] **Step 6: Run Rust unit and interactive capture gates**

```powershell
Set-Location D:\LLM\gui-tauri\src-tauri
cargo test --locked frame_buffer
cargo test --locked captures_primary_monitor_into_memory -- --ignored --nocapture
cargo check --locked
```

Expected: all pass on the active primary monitor and no image file appears in the worktree or `%TEMP%` from Ru.

- [ ] **Step 7: Commit the capture foundation**

```powershell
git add gui-tauri/src-tauri/Cargo.toml gui-tauri/src-tauri/Cargo.lock gui-tauri/src-tauri/src/frame_buffer.rs gui-tauri/src-tauri/src/screen_capture.rs gui-tauri/src-tauri/src/lib.rs
git commit -m "feat: add RAM-only primary screen capture"
```

---

### Task 5: Manage the local multimodal llama-server and parse typed observations

**Files:**
- Modify: `gui-tauri/src-tauri/Cargo.toml`
- Modify: `gui-tauri/src-tauri/Cargo.lock`
- Create: `gui-tauri/src-tauri/src/vision_runtime.rs`
- Modify: `gui-tauri/src-tauri/src/lib.rs`

**Interfaces:**
- Consumes: `PreparedFrame` batches of one to four frames and an existing Job Object assignment callback.
- Produces: `VisionObservation`, `VisionAnalyzer`, `VisionServerConfig`, and `LlamaVisionRuntime`.
- Never calls a non-loopback URL and never serializes frames to disk.

- [ ] **Step 1: Add the direct blocking HTTP dependency and failing command/payload tests**

```toml
reqwest = { version = "=0.13.4", default-features = false, features = ["blocking", "json", "rustls"] }
```

```rust
#[test]
fn server_command_uses_pinned_assets_loopback_and_single_slot() {
    let config = VisionServerConfig::for_project(Path::new(r"D:\LLM"), 18111);
    let args = config.args();
    assert!(args.windows(2).any(|pair| pair[0] == "--host" && pair[1] == "127.0.0.1"));
    assert!(args.windows(2).any(|pair| pair[0] == "--parallel" && pair[1] == "1"));
    assert!(args.iter().any(|value| value == "--mmproj"));
    assert!(args.iter().any(|value| value == "--no-webui"));
}

#[test]
fn request_contains_only_data_urls_and_json_schema() {
    let body = build_multimodal_request(&[prepared_jpeg(b"jpeg")]).unwrap();
    let encoded = serde_json::to_string(&body).unwrap();
    assert!(encoded.contains("data:image/jpeg;base64,"));
    assert!(encoded.contains("comment_worthiness"));
    assert!(!encoded.contains("http://") && !encoded.contains("https://"));
}
```

- [ ] **Step 2: Implement typed observations and validation**

```rust
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct VisionObservation {
    pub scene: String,
    pub event: String,
    pub confidence: f32,
    pub novelty: f32,
    pub comment_worthiness: f32,
    pub visible_text: Vec<String>,
}

#[derive(Debug)]
pub enum VisionError {
    MissingAsset,
    ServerStart,
    Http,
    InvalidSchema,
    Cancelled,
}

pub struct VisionServerConfig {
    pub executable: PathBuf,
    pub model: PathBuf,
    pub mmproj: PathBuf,
    pub port: u16,
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
```

Reject non-finite/out-of-range scores, scene over 500 characters, event over 2,000, more than 16 visible strings, any visible string over 200, and batches outside one to four frames.

- [ ] **Step 3: Implement server start, health, request, and bounded restart behavior**

`VisionServerConfig::for_project` resolves exact files from the pinned table and builds:

```text
llama-server.exe --model D:\LLM\models\vision\qwen3-vl-2b-instruct\Qwen3VL-2B-Instruct-Q4_K_M.gguf --mmproj D:\LLM\models\vision\qwen3-vl-2b-instruct\mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf --host 127.0.0.1 --port 18111 --ctx-size 4096 --n-predict 192 --parallel 1 --n-gpu-layers 99 --flash-attn on --cache-type-k q8_0 --cache-type-v q8_0 --image-max-tokens 512 --reasoning off --no-webui
```

`LlamaVisionRuntime::start` checks executable/file existence and exact byte size, chooses a free loopback port, spawns directly with null stdin/stdout/stderr, immediately assigns the child through `assign_child: &dyn Fn(&Child) -> io::Result<()>`, and waits at most 45 seconds for `/health`. On failure it kills/waits the exact owned child.

`analyze` posts `response_format: json_schema` to `/v1/chat/completions` with a 10-second HTTP timeout, parses `choices[0].message.content`, and validates the typed observation. No request body or decoded content appears in logs/errors.

- [ ] **Step 4: Test HTTP parsing and cleanup with a local fake server**

Use a `TcpListener` bound to `127.0.0.1:0` that captures one request and returns a fixed llama.cpp-shaped response. Assert Authorization is absent, host is loopback, image bytes appear only as the expected data URL, typed content parses, malformed JSON fails once, and dropping/stopping the runtime waits for its fake child.

- [ ] **Step 5: Run Rust tests and the live Qwen request**

```powershell
Set-Location D:\LLM\gui-tauri\src-tauri
cargo test --locked vision_runtime
cargo check --locked
Set-Location D:\LLM
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\benchmark_local_vision.py --runs 5
```

Expected: unit tests pass; all live responses validate; no vision process survives the runner.

- [ ] **Step 6: Commit the vision runtime**

```powershell
git add gui-tauri/src-tauri/Cargo.toml gui-tauri/src-tauri/Cargo.lock gui-tauri/src-tauri/src/vision_runtime.rs gui-tauri/src-tauri/src/lib.rs
git commit -m "feat: add local Qwen vision runtime"
```

---

### Task 6: Orchestrate capture, vision, backend reactions, metrics, single-instance, and shutdown

**Files:**
- Modify: `gui-tauri/src-tauri/Cargo.toml`
- Modify: `gui-tauri/src-tauri/Cargo.lock`
- Create: `gui-tauri/src-tauri/src/backend_bridge.rs`
- Create: `gui-tauri/src-tauri/src/observation_runtime.rs`
- Modify: `gui-tauri/src-tauri/src/lib.rs`

**Interfaces:**
- Consumes: `FrameSource`, `FrameRing`, `AdaptiveGate`, `VisionAnalyzer`, FastAPI observation endpoints, and `BackendState` token/base URL/Job Object.
- Produces Tauri commands: `observation_start`, `observation_stop`, `observation_status`, `observation_metrics`, `observation_voice_started`, `observation_voice_finished`.
- Produces events: `ru://observation-state` and `ru://screen-reaction`.

- [ ] **Step 1: Pin and register the official Tauri single-instance plugin first**

```toml
[target.'cfg(any(target_os = "macos", windows, target_os = "linux"))'.dependencies]
tauri-plugin-single-instance = "=2.4.3"
```

Move backend startup from before `tauri::Builder` into `.setup` so a second app instance cannot fail on port 8000 before the plugin rejects it. Register `tauri_plugin_single_instance::init` before every other plugin; its callback shows and focuses the existing `main` window.

```rust
let builder = tauri::Builder::default().plugin(tauri_plugin_single_instance::init(
    |app, _args, _cwd| {
        if let Some(window) = app.get_webview_window("main") {
            let _ = window.show();
            let _ = window.set_focus();
        }
    },
)).setup(|app| {
    app.manage(start_backend_state()?);
    app.manage(ObservationState::new());
    Ok(())
});
```

Map `io::Error` to a boxed setup error rather than using `expect`, so startup failure remains explicit. Keep `BackendRuntimeConfig` serialization unchanged and add crate-private `BackendState::runtime_config()` and `BackendState::assign_owned_child(&Child)` accessors; observation modules must not reach into its mutex fields.

- [ ] **Step 2: Write failing backend-bridge tests proving frames never reach FastAPI**

```rust
#[derive(Clone, Serialize)]
pub struct ReactionRequestBody {
    pub observation_id: String,
    pub event_age_ms: u64,
    pub observation: VisionObservation,
}

#[derive(Clone, Deserialize)]
pub struct Preflight {
    pub voice_label: String,
    pub voice_speed: f32,
}

#[derive(Clone, Deserialize)]
pub struct ReactionPayload {
    pub observation_id: String,
    pub text: String,
    pub voice_label: String,
    pub voice_speed: f32,
    pub cloud_ms: u64,
}

#[derive(Clone, Deserialize)]
pub struct ReactionReply {
    pub status: String,
    pub reason: Option<String>,
    pub reaction: Option<ReactionPayload>,
}

#[derive(Debug)]
pub enum BridgeError { Transport, Unauthorized, InvalidResponse, Provider }

pub trait ReactionClient: Send + Sync {
    fn preflight(&self) -> Result<Preflight, BridgeError>;
    fn react(&self, request: &ReactionRequestBody) -> Result<ReactionReply, BridgeError>;
    fn cancel(&self, observation_id: &str) -> Result<(), BridgeError>;
}
```

The fake-server test serializes `ReactionRequestBody` and asserts the body contains no `jpeg`, `image`, `base64`, `data:`, or frame byte field. `BackendBridge` attaches the existing bearer token and calls only `BackendRuntimeConfig.base_url` loopback endpoints.

- [ ] **Step 3: Write failing observation engine tests for lifecycle and worker policy**

Create test-only fake frame, vision, reaction, and event sinks with atomic counters. `FakeDependencies::events()` returns fixed labels (`preflight`, `capture_start`, `vision_start`, `vision_call`, `cancel`, `reaction_emit`, `vision_stop`) in call order; `active_workers()` and `buffered_bytes()` expose numeric leak checks. The critical loop test is:

```rust
#[test]
fn twenty_start_stop_cycles_leave_zero_workers_and_zero_buffered_bytes() {
    let dependencies = FakeDependencies::healthy();
    for _ in 0..20 {
        let mut runtime = ObservationRuntime::start_for_test(dependencies.clone()).unwrap();
        runtime.wait_until_observing(Duration::from_secs(1)).unwrap();
        runtime.stop();
    }
    assert_eq!(dependencies.active_workers(), 0);
    assert_eq!(dependencies.buffered_bytes(), 0);
    assert_eq!(dependencies.vision_starts(), dependencies.vision_stops());
}

#[test]
fn start_preflights_voice_before_capture_and_vision() {
    let dependencies = FakeDependencies::healthy();
    let mut runtime = ObservationRuntime::start_for_test(dependencies.clone()).unwrap();
    runtime.wait_until_observing(Duration::from_secs(1)).unwrap();
    assert_eq!(&dependencies.events()[..3], ["preflight", "capture_start", "vision_start"]);
    runtime.stop();
}

#[test]
fn repeated_vision_crashes_stop_after_two_restarts() {
    let dependencies = FakeDependencies::vision_always_crashes();
    let mut runtime = ObservationRuntime::start_for_test(dependencies.clone()).unwrap();
    runtime.wait_until_error(Duration::from_secs(5)).unwrap();
    assert_eq!(dependencies.vision_starts(), 3);
    assert_eq!(runtime.snapshot().restart_count, 2);
    runtime.stop();
}
```

Add matching focused tests that assert a newer high-value observation records `cancel` before its replacement request, a reaction sets `voice_reserved` before `reaction_emit`, and `stop()` joins every worker, clears the ring, and calls `vision_stop` exactly once.

- [ ] **Step 4: Implement the state/event contract**

```rust
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ObservationPhase { Off, Starting, Observing, Paused, Error }

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObservationSnapshot {
    pub phase: ObservationPhase,
    pub message: String,
    pub last_latency_ms: Option<u64>,
    pub comments_last_minute: usize,
    pub dropped_stale: u64,
    pub restart_count: u8,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObservationMetricsSnapshot {
    pub sample_count: usize,
    pub p50_ms: Option<u64>,
    pub p95_ms: Option<u64>,
    pub worst_ms: Option<u64>,
    pub dropped_stale: u64,
    pub comments_last_minute: usize,
}

pub struct ObservationState {
    runtime: Mutex<Option<ObservationRuntime>>,
    snapshot: Mutex<ObservationSnapshot>,
}

pub struct ObservationRuntime {
    stop: Arc<AtomicBool>,
    voice_reserved: Arc<AtomicBool>,
    workers: Vec<JoinHandle<()>>,
    buffered_bytes: Arc<AtomicUsize>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ScreenReactionEvent {
    pub observation_id: String,
    pub text: String,
    pub voice_label: String,
    pub voice_speed: f32,
    pub event_unix_ms: u64,
}
```

State messages are fixed UI status strings only; do not include observations or provider responses. `ObservationMetrics` retains at most 100 numeric samples and computes p50/p95/worst in memory.

- [ ] **Step 5: Implement three bounded workers**

1. Capture worker samples every 500 ms, recreates DXGI after `AccessLost`, prepares frames, pushes into a 20-frame ring, and sends the newest three-frame batch through a bounded capacity-one channel when `AdaptiveGate` fires.
2. Vision worker consumes the newest batch, pauses while voice is reserved, calls local Qwen, and replaces a pending reaction-channel item with a newer high-value observation.
3. Reaction worker cancels the previous backend request ID, computes `event_age_ms` with saturating subtraction from the selected frame's Unix-millisecond timestamp, sends structured text to `/observation/react`, rejects stale results, reserves voice, then emits `ru://screen-reaction`. Three consecutive cloud transport/provider failures set `paused`, close all three worker channels, clear the frame ring, and stop/wait the vision server so local chat fallback can claim the GPU. Policy skips such as `cooldown`, `repeat`, or `chat_busy` do not count as cloud failures.

Channels never grow beyond one pending batch/reaction. Stop uses `Arc<AtomicBool>`, closes channels, joins all three threads, clears the ring, and stops/waits the vision child. Restart delays are exactly one then three seconds; the third failure sets `error` without another spawn.

- [ ] **Step 6: Add Tauri commands and voice timing callbacks**

```rust
#[tauri::command]
fn observation_start(app: AppHandle, backend: State<BackendState>, state: State<ObservationState>) -> Result<ObservationSnapshot, String>;
#[tauri::command]
fn observation_stop(app: AppHandle, state: State<ObservationState>) -> Result<ObservationSnapshot, String>;
#[tauri::command]
fn observation_status(state: State<ObservationState>) -> ObservationSnapshot;
#[tauri::command]
fn observation_metrics(state: State<ObservationState>) -> ObservationMetricsSnapshot;
#[tauri::command]
fn observation_voice_started(observation_id: String, event_age_ms: u64, state: State<ObservationState>) -> Result<(), String>;
#[tauri::command]
fn observation_voice_finished(observation_id: String, played: bool, state: State<ObservationState>) -> Result<(), String>;
```

`observation_start` emits `starting`, performs backend preflight before DXGI/model work, and returns without blocking the UI thread while workers initialize. `voice_started` records event-to-first-playing latency; `voice_finished` releases the vision pause and counts only successfully played reactions.

- [ ] **Step 7: Integrate Job Object assignment and deterministic shutdown order**

Expose `BackendState::assign_owned_child(&Child)` while keeping the Job Object private. Start the vision server through that method. On `RunEvent::Exit`/`ExitRequested`, call `stop_observation` first, then the existing backend shutdown, then drop the Job Object so any descendant that ignored shutdown is killed by Windows.

Add/extend Rust tests proving a fake vision descendant dies when the Job Object drops and a second-instance callback focuses instead of spawning a second backend.

- [ ] **Step 8: Run Rust lifecycle gates**

```powershell
Set-Location D:\LLM\gui-tauri\src-tauri
cargo fmt --check
cargo test --locked
cargo check --locked
```

Expected: PASS; ignored interactive tests remain the only tests requiring a desktop/model.

- [ ] **Step 9: Commit the orchestration**

```powershell
git add gui-tauri/src-tauri/Cargo.toml gui-tauri/src-tauri/Cargo.lock gui-tauri/src-tauri/src/backend_bridge.rs gui-tauri/src-tauri/src/observation_runtime.rs gui-tauri/src-tauri/src/lib.rs
git commit -m "feat: orchestrate screen observation lifecycle"
```

---

### Task 7: Add the persistent observation control and mandatory OmniVoice playback

**Files:**
- Create: `gui-tauri/src/lib/observationClient.ts`
- Create: `gui-tauri/src/components/ObservationControl.tsx`
- Create: `gui-tauri/src/components/ObservationControl.test.tsx`
- Modify: `gui-tauri/src/lib/playVoice.ts`
- Modify: `gui-tauri/src/lib/playVoice.test.ts`
- Modify: `gui-tauri/src/App.tsx`
- Modify: `gui-tauri/src/App.css`

**Interfaces:**
- Consumes: the six Tauri commands and two events from Task 6.
- Produces: an always-mounted `ObservationControl` and `speakText(..., { interrupt, onPlaying })`.
- Screen reactions ignore the ordinary `ru_tts_enabled` preference because observation requires OmniVoice.

- [ ] **Step 1: Write failing voice tests for non-interruption and first-playing callback**

```typescript
it("does not interrupt existing speech for a proactive reaction", async () => {
  const first = speakText("first");
  expect(await speakText("screen event", { interrupt: false })).toBe(false);
  expect(mockAudio.pause).not.toHaveBeenCalled();
  finishCurrentAudio();
  await first;
});

it("reports the first playing event once", async () => {
  const onPlaying = vi.fn();
  const result = speakText("hello", { onPlaying });
  mockAudio.onplaying?.(new Event("playing"));
  mockAudio.onplaying?.(new Event("playing"));
  expect(onPlaying).toHaveBeenCalledTimes(1);
  finishCurrentAudio();
  await result;
});
```

- [ ] **Step 2: Extend `speakText` without changing chat defaults**

```typescript
type SpeakOptions = {
  voiceLabel?: string;
  speed?: number;
  interrupt?: boolean;
  onPlaying?: () => void;
};

export async function speakText(text: string, options: SpeakOptions = {}): Promise<boolean> {
  const busy = currentAbort !== null || currentAudio !== null;
  if (busy && options.interrupt === false) return false;
  if (options.interrupt !== false) stopVoice();
  // Existing fetch/blob/VTS lifecycle follows.
}
```

Set an `audio.onplaying` handler guarded by a local boolean and call `options.onPlaying` once. Keep current chat calls interrupting by default.

- [ ] **Step 3: Implement the typed observation client**

```typescript
export type ObservationPhase = "off" | "starting" | "observing" | "paused" | "error";
export type ObservationSnapshot = {
  phase: ObservationPhase;
  message: string;
  lastLatencyMs: number | null;
  commentsLastMinute: number;
  droppedStale: number;
  restartCount: number;
};
export type ScreenReactionEvent = {
  observationId: string;
  text: string;
  voiceLabel: string;
  voiceSpeed: number;
  eventUnixMs: number;
};
export type ObservationMetricsSnapshot = {
  sampleCount: number;
  p50Ms: number | null;
  p95Ms: number | null;
  worstMs: number | null;
  droppedStale: number;
  commentsLastMinute: number;
};

export const startObservation = () => invoke<ObservationSnapshot>("observation_start");
export const stopObservation = () => invoke<ObservationSnapshot>("observation_stop");
export const getObservationStatus = () => invoke<ObservationSnapshot>("observation_status");
export const getObservationMetrics = () => invoke<ObservationMetricsSnapshot>("observation_metrics");
export const reportVoiceStarted = (observationId: string, eventAgeMs: number) =>
  invoke("observation_voice_started", { observationId, eventAgeMs });
export const reportVoiceFinished = (observationId: string, played: boolean) =>
  invoke("observation_voice_finished", { observationId, played });
```

Wrap `listen` for both event names and return its unlisten callback.

- [ ] **Step 4: Write failing observation-control tests**

Tests must prove the control starts in `off`, maps all five status labels, disables duplicate start/stop clicks, remains mounted when tabs change, calls `speakText` even when `ru_tts_enabled` is `0`, passes `interrupt:false`, reports `onPlaying` age, reports failed playback, and unregisters both listeners on unmount.

```typescript
expect(speakText).toHaveBeenCalledWith("Anh hụt hơi nhiều rồi đó.", {
  voiceLabel: "Ru (OmniVoice)",
  speed: 1,
  interrupt: false,
  onPlaying: expect.any(Function),
});
```

- [ ] **Step 5: Implement the always-visible accessible control**

Mount `<ObservationControl />` once in `App`'s sidebar header, outside `renderTab()`. Use a real button with `aria-pressed`, a text status, and color plus icon/state text so color is never the only signal.

Exact user-facing mapping:

```typescript
const LABELS: Record<ObservationPhase, string> = {
  off: "Ru không quan sát",
  starting: "Ru đang mở mắt...",
  observing: "Ru đang nhìn màn hình chính",
  paused: "Observation tạm dừng",
  error: "Observation gặp lỗi",
};
```

Do not add a preview. Do not call `ttsEnabled()` for screen reactions. If playback returns false before `onPlaying`, report `played:false`; never append the reaction to chat messages. Refresh `observation_metrics` after each completed or failed proactive playback and expose its numeric p50/p95/worst values in the control's `title` and `data-latency-*` attributes so the live gate can inspect them without writing a report.

- [ ] **Step 6: Run frontend tests and build**

```powershell
Set-Location D:\LLM\gui-tauri
npm test -- --run
npm run build
```

Expected: PASS; TypeScript event/invoke payload names match the Rust camelCase contract.

- [ ] **Step 7: Commit the frontend control**

```powershell
git add gui-tauri/src/lib/observationClient.ts gui-tauri/src/components/ObservationControl.tsx gui-tauri/src/components/ObservationControl.test.tsx gui-tauri/src/lib/playVoice.ts gui-tauri/src/lib/playVoice.test.ts gui-tauri/src/App.tsx gui-tauri/src/App.css
git commit -m "feat: add screen observation control"
```

---

### Task 8: Run the live quality, VRAM, latency, cadence, and lifecycle gates

**Files:**
- Modify only if evidence exposes a defect: files from Tasks 1–7 plus their focused tests.
- Do not create or retain captured frame/content artifacts.

**Interfaces:**
- Consumes: the complete observation feature.
- Produces: console-reported numeric evidence and a pass/fail decision for the pinned Qwen candidate.

- [ ] **Step 1: Verify model/voice co-residency before a screen trial**

Start Ru through the normal Tauri launcher, select an OpenCode/Kilo free hosted model, keep the current Ru OmniVoice, and enable observation. In a separate terminal run read-only GPU/process snapshots:

```powershell
nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*D:\LLM*' } | Select-Object ProcessId,ParentProcessId,Name,CommandLine
```

Expected: exactly one Tauri-owned backend tree, one vision llama-server only while observing, one owned OmniVoice server, no local 8B llama-server, and no CUDA OOM. Stop execution and revise the plan if Qwen+OmniVoice cannot coexist.

- [ ] **Step 2: Run a ten-minute primary-monitor behavior trial**

Exercise all of these live scenes without saving them: active game movement, a clear win/loss transition, repeated similar misses, YouTube with visible subtitles, a static desktop, and a rapid application switch. Inspect the always-visible in-memory metrics.

Acceptance:

- Qwen descriptions match the event often enough for the cloud line to make sense.
- Active periods average three to six successfully played distinct reactions per minute.
- Static scenes back off and repeated events do not produce repetitive bursts.
- No reaction appears in chat history, session files, memory, approved examples, or logs.
- Every reaction is the current Ru OmniVoice and may overlap game/video sound.
- No reaction offers code help, debugging, technical diagnosis, or gameplay instruction.

- [ ] **Step 3: Record latency without content**

Read the observation control's numeric metrics tooltip/`data-latency-*` attributes and report only these fields:

```json
{"sampleCount": 0, "p50Ms": 0, "p95Ms": 0, "worstMs": 0, "droppedStale": 0, "commentsLastMinute": 0}
```

Pass requires p50 ≤ 2,500 ms. Report p95/worst honestly. Free-cloud outliers may exceed three seconds but must be dropped once stale rather than spoken late.

- [ ] **Step 4: Run resource and cleanup loops**

Toggle observation on/off 20 times, then open/close the entire app 10 times through the UI. After each complete close, run:

```powershell
Start-Sleep -Seconds 3
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*D:\LLM*' } | Select-Object ProcessId,ParentProcessId,Name,CommandLine
nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits
```

Expected: no Tauri-owned descendant remains after close; repeated observation cycles do not monotonically increase RAM, VRAM, handles, worker count, or buffer bytes. Do not kill unrelated pre-existing processes as part of this gate.

- [ ] **Step 5: Verify cloud, voice, VTS, display, crash, and single-instance failures**

Run each path once:

1. Start a second Ru instance: the existing window focuses and no second backend/model tree appears.
2. Disable the cloud route: observation becomes `paused`, vision releases, then local chat fallback may load.
3. Make OmniVoice unavailable: no screen reaction is shown/spoken by another voice and status becomes actionable error.
4. Close VTube Studio: OmniVoice still speaks without animation.
5. Change primary display topology: the ring clears and capture recreates only the new primary source.
6. Force-close the Tauri parent during active observation: Job Object removes backend, vision, and owned voice descendants.

- [ ] **Step 6: Stop cleanly at any failed hard gate**

If any live hard gate fails, stop this plan with the worktree intact, report the exact numeric/error evidence, and run `superpowers:systematic-debugging` before authoring a focused plan amendment. Do not make an unplanned model switch or relax the 2.5-second median, RAM-only, process-cleanup, or mandatory-OmniVoice gates to make a failure appear green.

---

### Task 9: Update active documents and run the complete verification matrix

**Files:**
- Modify: `README.md`
- Modify: `gui-tauri/README.md`
- Modify: `TODO.md`
- Modify: `docs/ANTIGRAVITY_HANDOFF.md`
- Modify: `docs/superpowers/specs/2026-08-01-ru-local-screen-awareness-design.md`

**Interfaces:**
- Consumes: actual Task 8 evidence only.
- Produces: current setup/behavior/status documentation with no claim beyond measured results.

- [ ] **Step 1: Update the active documentation truth**

Document:

- exact explicit model setup command and pinned model/license/source;
- primary-monitor-only, manual/off-by-default observation UI;
- RAM-only frame behavior and the fact that unfiltered textual observations go to the selected cloud provider;
- mandatory current Ru OmniVoice and no system-audio support;
- provider/voice/VTS/fallback failure behavior;
- actual measured p50/p95/worst, cadence, peak VRAM, and the date/machine;
- process-tree/single-instance cleanup behavior;
- the command to rerun focused model/capture/full tests.

Change the design status from approved to implemented only if every hard gate passed. Mark the roadmap checkbox complete only then. If a hard gate failed, record the exact blocked gate and leave the roadmap item open.

- [ ] **Step 2: Remove or correct stale active-document statements**

```powershell
rg -n -i "vision work after|screen awareness.*not implemented|voice input/output after|Gwen is connected|sensitive.*screen|system audio" README.md TODO.md gui-tauri\README.md docs\ANTIGRAVITY_HANDOFF.md docs\superpowers\specs -g "*.md"
```

Preserve historical measurements/specs but add a historical/superseded status where an old document could be mistaken for current runtime guidance. Delete only a document that is purely obsolete and has no historical value.

- [ ] **Step 3: Run all automated gates sequentially**

```powershell
Set-Location D:\LLM
& D:\LLM\.venv\Scripts\python.exe -m pytest -q

Set-Location D:\LLM\gui-tauri
npm test -- --run
npm run build

Set-Location D:\LLM\gui-tauri\src-tauri
cargo fmt --check
cargo test --locked
cargo check --locked

Set-Location D:\LLM
git diff --check
```

Expected: every command exits zero.

- [ ] **Step 4: Inspect scope, secrets, artifacts, and worktree**

```powershell
git status --short
git diff --stat
git diff -- . ':(exclude)docs/superpowers/plans/2026-08-02-ru-local-screen-awareness.md'
git ls-files models eval_results logs .hf
rg -n "PRIVATE MESSAGE|data:image/jpeg;base64|observation.*event" logs data . -g "*.log" -g "*.json" -g "*.jsonl"
```

Expected: no model, frame, observation, reaction, API key, live report, or private content is staged/tracked. Review every match rather than deleting user data.

- [ ] **Step 5: Commit the verified documentation**

```powershell
git add README.md gui-tauri/README.md TODO.md docs/ANTIGRAVITY_HANDOFF.md docs/superpowers/specs/2026-08-01-ru-local-screen-awareness-design.md
git commit -m "docs: document local screen awareness"
```

- [ ] **Step 6: Final repository evidence**

```powershell
git status --short
git log --oneline --decorate -12
git rev-list --left-right --count origin/main...HEAD
```

Expected before any separately authorized push: clean worktree and only the reviewed local commits ahead of `origin/main`.
