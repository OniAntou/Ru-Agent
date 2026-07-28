# Ru OpenCode Streaming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stream OpenCode reply text into one safe pending Ru bubble and make 512 tokens plus online search the default.

**Architecture:** `OpenAIRuntime.generate` consumes the existing completion as SSE and emits deltas while accumulating the final response. `ModelRunner` forwards deltas and resets the GUI stream before a second, search-result completion. GUI presentation owns pure hiding of partial VTS and search markers; Tkinter owns only the pending-bubble rendering and final replacement.

**Tech Stack:** Python 3.13, urllib SSE, Tkinter, pytest.

## Global Constraints

- Keep local GGUF and Transformers behavior unchanged.
- Do not make an extra OpenCode request for streaming, timing, or defaults.
- Never display a full or partial `[[VTS:` or `[SEARCH:` control marker.
- Only final cleaned response text may reach history, memory, VTS dispatch, or TTS.
- New GUI sessions default to 512 tokens and enabled online search; Settings may still override both.

---

### Task 1: Stream the existing OpenCode completion request

**Files:**

- Modify: `src/mini_gpt/openai_runtime.py:60-113`
- Modify: `tests/test_openai_runtime.py`

**Interfaces:**

- `OpenAIRuntime.generate(..., on_delta: Callable[[str], None] | None = None, on_complete_timing: Callable[[float], None] | None = None) -> str`
- Each `on_delta` value is a non-empty `choices[0].delta.content` fragment.

- [ ] **Step 1: Write a failing SSE test**

```python
def test_generate_streams_deltas_and_returns_the_complete_reply(monkeypatch) -> None:
    class Response:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def __iter__(self):
            return iter((
                b'data: {"choices":[{"delta":{"content":"Em "}}]}\n',
                b'data: {"choices":[{"delta":{"content":"chao anh"}}]}\n',
                b'data: [DONE]\n',
            ))

    captured_request = []
    monkeypatch.setattr("mini_gpt.openai_runtime.urlopen", lambda request, **_kwargs: captured_request.append(request) or Response())
    deltas = []
    result = OpenAIRuntime("key").generate(
        [("user", "Xin chao")], 32, 0.7, 20, on_delta=deltas.append
    )

    assert result == "Em chao anh"
    assert deltas == ["Em ", "chao anh"]
    assert json.loads(captured_request[0].data)["stream"] is True
```

- [ ] **Step 2: Run the new test and verify it fails**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py -q`

Expected: FAIL because `generate` does not accept `on_delta` and sends no stream flag.

- [ ] **Step 3: Implement SSE parsing without a second request**

```python
body["stream"] = True
parts: list[str] = []
with urlopen(request, timeout=120) as response:
    for raw_line in response:
        line = raw_line.decode("utf-8").strip()
        if not line.startswith("data: "):
            continue
        payload = line[6:]
        if payload == "[DONE]":
            break
        delta = json.loads(payload).get("choices", [{}])[0].get("delta", {}).get("content")
        if isinstance(delta, str) and delta:
            parts.append(delta)
            if on_delta is not None:
                on_delta(delta)
return normalize_text("".join(parts))
```

Keep HTTP/URL error text and `on_complete_timing` behavior; malformed SSE JSON raises the existing OpenCode invalid-JSON style error.

- [ ] **Step 4: Run the runtime tests and commit**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py -q`

```powershell
git add -- src/mini_gpt/openai_runtime.py tests/test_openai_runtime.py
git commit -m "feat: stream Ru OpenCode completions"
```

### Task 2: Make streamed control markers invisible

**Files:**

- Modify: `src/mini_gpt/gui_presentation.py:1-52`
- Modify: `tests/test_gui_presentation.py`

**Interfaces:**

- `safe_streamed_response(raw: str) -> str` returns only text that can safely be rendered before response completion.

- [ ] **Step 1: Write failing safe-stream tests**

```python
def test_safe_streamed_response_holds_partial_private_markers():
    assert safe_streamed_response("Em chao anh [") == "Em chao anh "
    assert safe_streamed_response("Em chao anh [[VTS:") == "Em chao anh "
    assert safe_streamed_response("Em chao anh [[VTS:vts_04]] Nya~") == "Em chao anh "
    assert safe_streamed_response("[SEAR") == ""
    assert safe_streamed_response("[SEARCH: weather]") == ""
```

- [ ] **Step 2: Run the presentation test and verify it fails**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui_presentation.py -q`

Expected: FAIL because `safe_streamed_response` is absent.

- [ ] **Step 3: Implement marker-safe rendering**

Use the exact private markers `[[VTS:` and `[SEARCH:`. Return the prefix before
either complete marker. Otherwise, remove the longest suffix that is a prefix
of either marker, case-insensitively, so a split marker never flashes.

```python
def safe_streamed_response(raw: str) -> str:
    markers = ("[[VTS:", "[SEARCH:")
    marker_positions = [raw.casefold().find(marker.casefold()) for marker in markers]
    positions = [position for position in marker_positions if position >= 0]
    if positions:
        return raw[:min(positions)]
    for length in range(max(map(len, markers)) - 1, 0, -1):
        tail = raw[-length:].casefold()
        if any(marker.casefold().startswith(tail) for marker in markers):
            return raw[:-length]
    return raw
```

- [ ] **Step 4: Run presentation tests and commit**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui_presentation.py -q`

```powershell
git add -- src/mini_gpt/gui_presentation.py tests/test_gui_presentation.py
git commit -m "feat: hide private control markers while streaming"
```

### Task 3: Stream one pending GUI bubble and change defaults

**Files:**

- Modify: `src/mini_gpt/gui.py:34,54-64,239-319,373-401,673-681,847-1000`
- Modify: `tests/test_gui.py`
- Modify: `README.md`

**Interfaces:**

- `ModelRunner.generate_reply(..., on_stream_delta: Callable[[str], None] | None = None, on_stream_reset: Callable[[], None] | None = None) -> str`
- `RuApp._begin_streaming_turn(prompt) -> None`, `_append_stream_delta(delta) -> None`, `_reset_streaming_turn() -> None`, `_finish_streaming_turn(response: str) -> None`, and `_discard_streaming_turn() -> None` are main-thread-only helpers.

- [ ] **Step 1: Write failing model-runner, default, and transcript tests**

```python
def test_model_runner_resets_stream_before_the_second_search_completion(monkeypatch):
    calls, deltas, resets = [], [], []

    class Runtime:
        def generate(self, _messages, *_args, on_delta=None, **_kwargs):
            calls.append(1)
            if len(calls) == 1:
                on_delta("[SEARCH: weather]")
                return "[SEARCH: weather]"
            on_delta("Troi dep.")
            return "Troi dep."

    monkeypatch.setattr(gui, "perform_web_search", lambda _query: "sunny")
    runner = ModelRunner()
    runner.backend = BACKEND_OPENCODE
    runner.openai_runtime = Runtime()
    assert runner.generate_reply("Thoi tiet?", 512, 0.7, 20, allow_web_search=True,
        on_stream_delta=deltas.append, on_stream_reset=lambda: resets.append("reset")) == "Troi dep."
    assert deltas == ["[SEARCH: weather]", "Troi dep."]
    assert resets == ["reset"]


def test_gui_defaults_to_512_tokens_and_enabled_online_search():
    assert gui.DEFAULT_MAX_NEW_TOKENS == "512"
    assert gui.DEFAULT_ALLOW_WEB_SEARCH is True
```

Use this fake-transcript test for the pending-bubble seam:

```python
def test_pending_stream_replaces_one_ru_bubble_with_final_clean_text():
    app = object.__new__(gui.RuApp)
    app._has_empty_state = False
    app._stream_raw_response = ""
    app.transcript_text = FakeTranscript()

    app._begin_streaming_turn("Hi")
    app._append_stream_delta("Em chao ")
    app._append_stream_delta("anh")
    app._finish_streaming_turn("Em chao anh Nya~")

    assert app.transcript_text.ru_responses == ["Em chao anh Nya~"]
```

`FakeTranscript` records every insertion tagged `ru_bubble`, treats `tk.END`
as its current text boundary, and truncates the pending response when `delete`
starts at `app._stream_response_start`. Its final list must contain one item,
proving that finalization replaces rather than appends a second bubble.

- [ ] **Step 2: Run GUI tests and verify they fail**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q`

Expected: FAIL because streaming callbacks, pending-bubble helpers, and the
new defaults do not exist.

- [ ] **Step 3: Forward the stream through ModelRunner**

Pass `on_stream_delta` through `_generate` only for OpenCode. For a first
completion matching `[SEARCH: ...]`, call `on_stream_reset` before the existing
second completion. For a normal completion, do not reset. Local backend calls
must receive no stream callback.

- [ ] **Step 4: Render and finalize a single pending bubble on the Tk main thread**

At cloud turn start, schedule `_begin_streaming_turn(prompt)`. From the worker,
schedule each `on_stream_delta` with `root.after(0, ...)`; `_append_stream_delta`
accumulates raw deltas and renders `safe_streamed_response(raw)`. Capture the
text index where the pending Ru response begins, delete only from that index to
`tk.END` on update, and insert the replacement with `ru_bubble` styling.

Pass `streamed=is_cloud` into `_generation_finished`. After existing
`parse_catalog_reply`, call `_finish_streaming_turn(clean_result)` instead of
`_append_turn`; it replaces the bubble with the complete clean reply. On any
generation error, call `_discard_streaming_turn` before setting the status.
Only after finalization may the existing memory, VTS dispatch, and TTS code run.

- [ ] **Step 5: Change defaults and document them**

```python
DEFAULT_MAX_NEW_TOKENS = "512"
DEFAULT_ALLOW_WEB_SEARCH = True
# in __init__
self.allow_web_search_var = tk.BooleanVar(value=DEFAULT_ALLOW_WEB_SEARCH)
```

Document that direct OpenCode replies stream visibly, private VTS/search
control text remains hidden, and Settings can lower the token limit or disable
search.

- [ ] **Step 6: Run GUI and documentation verification, then commit**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_gui_presentation.py tests\test_openai_runtime.py -q
& .\.venv\Scripts\python.exe -m compileall -q src\mini_gpt
rg -n "128 tokens|one-WAV prefetch" README.md
```

Expected: tests and compilation pass; README has no stale default-token or
prefetch description.

```powershell
git add -- src/mini_gpt/gui.py tests/test_gui.py README.md
git commit -m "feat: stream Ru OpenCode replies by default"
```
