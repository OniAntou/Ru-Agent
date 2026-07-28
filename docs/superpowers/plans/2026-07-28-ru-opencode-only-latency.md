# Ru OpenCode-only latency implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove OpenAI API as a Ru backend and display a safe phase-by-phase latency breakdown for OpenCode replies.

**Architecture:** Keep `OpenAIRuntime` as an OpenAI-compatible transport but create it only with the fixed OpenCode Zen URL. A callback captures the elapsed duration of the one existing HTTP completion request. GUI captures Gwen warmup and first-WAV synthesis around current calls; it never makes a measurement request.

**Tech Stack:** Python 3.13, urllib, Tkinter, Flask, pytest.

## Global Constraints

- Keep GGUF and Transformers unchanged.
- Migrate saved `OpenAI API` to `OpenCode Zen API`.
- Do not log API keys, headers, prompts, or model output.
- Do not make extra HTTP requests for timing.
- Local replies omit cloud-only latency fields.

---

### Task 1: Time the existing OpenCode completion request

**Files:**

- Modify: `src/mini_gpt/openai_runtime.py:52-94`
- Create: `tests/test_openai_runtime.py`

**Interfaces:**

- Changes `OpenAIRuntime.generate(..., on_complete_timing: Callable[[float], None] | None = None) -> str`.
- The callback receives one non-negative elapsed value after the existing response body is fully read.

- [ ] **Step 1: Write the failing timing callback test**

```python
def test_generate_reports_one_complete_request_duration(monkeypatch) -> None:
    from mini_gpt.openai_runtime import OpenAIRuntime

    class Response:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def read(self): return b'{"choices":[{"message":{"content":"Ru"}}]}'

    ticks = iter((10.0, 12.5))
    monkeypatch.setattr("mini_gpt.openai_runtime.urlopen", lambda *_args, **_kwargs: Response())
    monkeypatch.setattr("mini_gpt.openai_runtime.perf_counter", ticks.__next__)
    timings: list[float] = []

    assert OpenAIRuntime("key", "https://opencode.ai/zen/v1", "deepseek-v4-flash").generate(
        [("user", "Xin chao")], 32, 0.7, 20, on_complete_timing=timings.append
    ) == "Ru"
    assert timings == [2.5]
```

- [ ] **Step 2: Run the new test**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py -q`

Expected: FAIL because `generate` has no timing callback.

- [ ] **Step 3: Implement timing around the one existing request**

```python
from time import perf_counter
from collections.abc import Callable

def generate(self, messages, max_new_tokens, temperature, top_k, *, on_complete_timing: Callable[[float], None] | None = None) -> str:
    started = perf_counter()
    with urlopen(request, timeout=120) as response:
        response_body = response.read().decode("utf-8")
    if on_complete_timing is not None:
        on_complete_timing(perf_counter() - started)
    # retain existing JSON validation and content return
```

Invoke the callback only after a successful full body read; leave current exception messages unchanged.

- [ ] **Step 4: Run runtime tests and commit**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py -q`

```powershell
git add -- src/mini_gpt/openai_runtime.py tests/test_openai_runtime.py
git commit -m "feat: measure OpenCode completion latency"
```

### Task 2: OpenCode-only GUI and latency presentation

**Files:**

- Modify: `src/mini_gpt/gui.py:54-65, 194-291, 311-364, 853-932, 969-1048`
- Modify: `src/mini_gpt/gui_presentation.py:35-37`
- Modify: `tests/test_gui.py`
- Create: `tests/test_gui_presentation.py`

**Interfaces:**

- Produces `format_generation_status(cache_hit: bool, seconds: float, api_seconds: float | None = None, gwen_warm_seconds: float | None = None) -> str`.
- `_play_gwen_chunks(..., on_first_audio_timing: Callable[[float], None] | None = None)` invokes its callback after the first WAV returns.

- [ ] **Step 1: Write failing presentation and migration tests**

```python
def test_cloud_status_includes_latency_breakdown() -> None:
    assert format_generation_status(
        cache_hit=True, seconds=2.4, api_seconds=2.1, gwen_warm_seconds=0.2
    ) == "Ru replied in 2.4 s | API 2.1 s | Gwen warm 0.2 s"

def test_backend_labels_offer_opencode_but_not_openai() -> None:
    assert "OpenCode Zen API" in gui.BACKEND_LABELS
    assert "OpenAI API" not in gui.BACKEND_LABELS
```

- [ ] **Step 2: Run focused tests**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_gui_presentation.py -q`

Expected: FAIL because OpenAI remains and the formatter lacks phase fields.

- [ ] **Step 3: Implement migration, fixed URL, and phase propagation**

Remove `BACKEND_OPENAI`, the URL input/control, OpenAI label, and its model-runner branch. In `RuApp.__init__`, map a saved backend equal to `OpenAI API` to `OpenCode Zen API`; persist only that cloud backend. Keep API key/model and Fetch, with its URL fixed to `https://opencode.ai/zen/v1`.

Use this exact timing flow:

```python
gwen_warm_seconds = None
api_seconds = None
if backend == BACKEND_OPENCODE and self.tts_enabled_var.get():
    warm_started = perf_counter()
    request_gwen_control("/warmup", {"voice_profile": self._selected_voice_profile()})
    gwen_warm_seconds = perf_counter() - warm_started

def record_api(seconds: float) -> None:
    nonlocal api_seconds
    api_seconds = seconds

result = self.runner.generate_reply(..., on_complete_timing=record_api)
```

Pass fields to `_generation_finished` and then `format_generation_status`. For local backends pass `None`. After first generated WAV, update the status with `first voice N s` while retaining API/warm values; do not time playback.

- [ ] **Step 4: Run GUI tests and commit**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_gui_presentation.py -q`

```powershell
git add -- src/mini_gpt/gui.py src/mini_gpt/gui_presentation.py tests/test_gui.py tests/test_gui_presentation.py
git commit -m "feat: make Ru OpenCode-only with latency breakdown"
```

### Task 3: Documentation and acceptance

**Files:**

- Modify: `README.md`

- [ ] **Step 1: Document OpenCode-only cloud use**

State that OpenCode Zen is the sole cloud option and explain that displayed API duration is full request-to-complete-response time, not first-token latency. Explain that Gwen warm and first voice are separate local measurements.

- [ ] **Step 2: Run final verification**

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py tests\test_gui.py tests\test_gui_presentation.py tests\test_tts_chunks.py -q
& .\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q
& .\.venv\Scripts\python.exe -m compileall -q src\mini_gpt gwen_tts_server.py
rg -n "OpenAI API|api.openai.com" src\mini_gpt README.md
git diff --check
```

Expected: all tests/compilation pass and the search has no product match.

- [ ] **Step 3: Manual latency acceptance**

Restart GUI, send the same short Vietnamese prompt five times after Gwen is warm, and record API/warm/first-voice fields. If API varies while warm and first voice stay stable, the provider/model path is the cause; if warm or first voice varies, diagnose local Gwen instead.

- [ ] **Step 4: Commit documentation**

```powershell
git add -- README.md
git commit -m "docs: explain Ru OpenCode latency fields"
```
