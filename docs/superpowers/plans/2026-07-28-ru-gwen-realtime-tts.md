# Ru Gwen near-realtime TTS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Start playing a short cloud-backed Ru reply in under two seconds when Gwen is warm, while keeping the existing Vietnamese clone voice and VTube lip-sync.

**Architecture:** Retain one Gwen model and one precomputed `VoiceClonePromptItem` in the owned HTTP service while a cloud backend is active. Split display-safe reply text into safe sentence chunks; a single producer synthesizes them in FIFO order while the playback consumer starts the first WAV immediately and consumes later WAVs as they become ready. Release Gwen before any local Llama generation.

**Tech Stack:** Python 3.13, Flask, `qwen_tts`, PyTorch CUDA, Tkinter, pygame, pytest.

---

## File structure

- `src/mini_gpt/tts_chunks.py`: Pure sentence chunking and queue-safe text helpers; no Tkinter, network, or audio dependencies.
- `gwen_tts_server.py`: Resident model/clone-prompt lifecycle plus `/warmup` and `/release` endpoints.
- `src/mini_gpt/gui.py`: Cloud lifecycle calls, sentence audio queue, playback lifecycle, and GPU handoff before local inference.
- `tests/test_tts_chunks.py`: Deterministic chunking coverage.
- `tests/test_gwen_tts_server.py`: Cached prompt and service endpoint coverage.
- `tests/test_gui.py`: GUI-side HTTP lifecycle and ordered queue coverage without loading CUDA or playing audio.
- `README.md`: Accurate setup and behaviour notes, including the cloud-only resident-Gwen rule and latency measurement command.

### Task 1: Sentence chunking primitive

**Files:**
- Create: `src/mini_gpt/tts_chunks.py`
- Create: `tests/test_tts_chunks.py`

- [x] **Step 1: Write the failing sentence-boundary tests**

```python
from mini_gpt.tts_chunks import split_tts_chunks


def test_split_tts_chunks_keeps_sentences_in_order() -> None:
    assert split_tts_chunks("Em chao anh. Hom nay em khoe!", max_chars=80) == [
        "Em chao anh.", "Hom nay em khoe!"
    ]


def test_split_tts_chunks_splits_long_sentence_at_word_boundary() -> None:
    chunks = split_tts_chunks("mot " * 40, max_chars=40)
    assert chunks[0] == "mot mot mot mot mot mot mot mot mot mot"
    assert all(len(chunk) <= 40 for chunk in chunks)
    assert " ".join(chunks).split() == ("mot " * 40).split()


def test_split_tts_chunks_discards_empty_segments() -> None:
    assert split_tts_chunks("   \n\n  ", max_chars=80) == []
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_tts_chunks.py -q`

Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.tts_chunks'`.

- [x] **Step 3: Implement the pure chunker**

```python
# src/mini_gpt/tts_chunks.py
from __future__ import annotations

import re


def split_tts_chunks(text: str, *, max_chars: int = 120) -> list[str]:
    if max_chars < 20:
        raise ValueError("max_chars must be at least 20")
    sentences = re.split(r"(?<=[.!?…])\s+|\n+", text.strip())
    chunks: list[str] = []
    for sentence in (part.strip() for part in sentences):
        while len(sentence) > max_chars:
            boundary = sentence.rfind(" ", 0, max_chars + 1)
            if boundary <= 0:
                boundary = max_chars
            chunks.append(sentence[:boundary].strip())
            sentence = sentence[boundary:].strip()
        if sentence:
            chunks.append(sentence)
    return chunks
```

- [x] **Step 4: Run the focused tests to verify they pass**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_tts_chunks.py -q`

Expected: `3 passed`.

- [x] **Step 5: Commit the pure helper**

```powershell
git add -- src/mini_gpt/tts_chunks.py tests/test_tts_chunks.py
git commit -m "feat: split Ru speech into safe chunks"
```

### Task 2: Warm Gwen service and cache the clone prompt

**Files:**
- Modify: `gwen_tts_server.py:19-81`
- Modify: `tests/test_gwen_tts_server.py`

- [x] **Step 1: Write failing lifecycle and cached-prompt tests**

```python
def test_warmup_reuses_cached_voice_clone_prompt(monkeypatch) -> None:
    import gwen_tts_server as server

    created: list[str] = []

    class Model:
        def create_voice_clone_prompt(self, **_kwargs):
            created.append("prompt")
            return ["cached-prompt"]

    monkeypatch.setattr(server, "get_model", lambda: Model())
    server._voice_clone_prompt = None

    assert server.warm_model() == {"ready": True}
    assert server.warm_model() == {"ready": True}
    assert created == ["prompt"]


def test_release_endpoint_clears_cached_model_and_prompt(monkeypatch) -> None:
    import gwen_tts_server as server

    released: list[bool] = []
    monkeypatch.setattr(server, "release_model", lambda: released.append(True))

    response = server.create_app().test_client().post("/release")

    assert response.status_code == 200
    assert response.get_json() == {"released": True}
    assert released == [True]
```

- [x] **Step 2: Run server tests to verify they fail**

Run: `& .\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q`

Expected: FAIL because `warm_model` and `release_model` do not exist.

- [x] **Step 3: Implement resident lifecycle and endpoints**

```python
_model = None
_voice_clone_prompt = None


def warm_model() -> dict[str, bool]:
    global _voice_clone_prompt
    model = get_model()
    if _voice_clone_prompt is None:
        _voice_clone_prompt = model.create_voice_clone_prompt(
            ref_audio=str(REFERENCE_AUDIO),
            ref_text=REFERENCE_TEXT.read_text(encoding="utf-8").strip(),
        )
    return {"ready": True}


def release_model() -> None:
    global _model, _voice_clone_prompt
    _voice_clone_prompt = None
    _model = None
    import gc
    import torch
    gc.collect()
    torch.cuda.empty_cache()
```

Update `synthesize_fast` to call `warm_model()` and pass
`voice_clone_prompt=_voice_clone_prompt` to `generate_voice_clone`; do not pass
the reference file again. Add `POST /warmup` returning `warm_model()` and
`POST /release` returning `{"released": True}` after `release_model()`.

- [x] **Step 4: Run the server tests to verify they pass**

Run: `& .\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q`

Expected: all Gwen server tests pass.

- [x] **Step 5: Commit the service lifecycle**

```powershell
git add -- gwen_tts_server.py tests/test_gwen_tts_server.py
git commit -m "feat: keep warm Gwen clone prompt on CUDA"
```

### Task 3: Cloud-only warmup and ordered audio queue

**Files:**
- Modify: `src/mini_gpt/gui.py:39-123, 749-952`
- Modify: `tests/test_gui.py`
- Modify: `tests/test_tts_chunks.py`

- [x] **Step 1: Write failing GUI helper tests**

```python
def test_gwen_control_posts_to_warmup(monkeypatch) -> None:
    requested: list[str] = []

    class Response:
        status_code = 200
        @staticmethod
        def json(): return {"ready": True}

    assert gui.request_gwen_control("/warmup", post=lambda url, **_kwargs: requested.append(url) or Response()) == {"ready": True}
    assert requested == ["http://127.0.0.1:5051/warmup"]


def test_play_gwen_chunks_requests_audio_in_order(monkeypatch, tmp_path) -> None:
    app = object.__new__(gui.RuApp)
    app._speech_generation = 1
    app.root = type("Root", (), {"after": lambda _self, _delay, callback: callback()})()
    app.status_var = type("Status", (), {"set": lambda _self, _text: None})()
    app.vts_manager = type("Vts", (), {"is_speaking": False})()
    requested: list[str] = []
    monkeypatch.setattr(gui, "request_tts_audio", lambda text, **_kwargs: requested.append(text) or str(tmp_path / f"{len(requested)}.wav"))
    monkeypatch.setattr(gui, "play_wav_blocking", lambda *_args, **_kwargs: None)

    app._play_gwen_chunks(["Cau dau.", "Cau sau."], fast_mode=True)

    assert requested == ["Cau dau.", "Cau sau."]
```

- [x] **Step 2: Run GUI tests to verify they fail**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q`

Expected: FAIL because `request_gwen_control`, `play_wav_blocking`, and
`_play_gwen_chunks` do not exist.

- [x] **Step 3: Implement service control and the queue**

```python
GWEN_TTS_BASE_URL = "http://127.0.0.1:5051"


def request_gwen_control(path: str, *, post=None) -> dict[str, bool]:
    import requests
    response = (post or requests.post)(f"{GWEN_TTS_BASE_URL}{path}", timeout=30)
    if response.status_code != 200:
        raise RuntimeError("Gwen voice service rejected lifecycle request.")
    return response.json()
```

In `RuApp._generate_worker`, before `runner.load`, branch on `backend`:

```python
is_cloud = backend in (BACKEND_OPENAI, BACKEND_OPENCODE)
if is_cloud and self.tts_enabled_var.get() and self.tts_engine_var.get() == "Gwen TTS (Voice Clone)":
    self.runner.release_gpu()
    ensure_gwen_tts_server()
    request_gwen_control("/warmup")
elif not is_cloud and gwen_tts_server_available():
    request_gwen_control("/release")
```

Replace the Gwen block in `_play_tts` with
`self._play_gwen_chunks(split_tts_chunks(clean_text), fast_mode=is_cloud)`.
Initialize `self._speech_generation = 0` in `RuApp.__init__`, then increment it
at the start of `_start_generation` to cancel prior queued speech. Implement a
producer/consumer queue, not a sequential synthesis loop: the producer is the
only caller of `request_tts_audio`, places each resulting path into a
`queue.Queue` (capacity one) and finally sends a sentinel. The consumer starts
playing as soon as the first path arrives while the producer synthesizes the
next chunk. Both sides must stop on a newer `_speech_generation`, delete every
temporary WAV, and clear `vts_manager.is_speaking` exactly once on completion,
cancellation, or failure. The public method remains:

```python
def _play_gwen_chunks(self, chunks: list[str], *, fast_mode: bool) -> None:
    ...
```

The test must use a zero-delay fake playback function and assert that the
producer requests `Cau dau.` before `Cau sau.` without CUDA or real audio.

- [x] **Step 4: Run GUI and chunk tests to verify they pass**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_tts_chunks.py -q`

Expected: all selected tests pass with no CUDA model load and no real audio
playback.

- [x] **Step 5: Commit GUI queue and handoff**

```powershell
git add -- src/mini_gpt/gui.py src/mini_gpt/tts_chunks.py tests/test_gui.py tests/test_tts_chunks.py
git commit -m "feat: queue warm Gwen speech for cloud replies"
```

### Task 4: Runtime proof and documentation

**Files:**
- Modify: `README.md:114-133`
- Modify: `tests/test_gwen_tts_server.py`

- [x] **Step 1: Add a timing assertion seam to the service response**

```python
started = perf_counter()
audio_path = synthesize_fast(text)
elapsed_ms = round((perf_counter() - started) * 1000)
return jsonify({"audio_path": audio_path, "synthesis_ms": elapsed_ms})
```

Add a test that stubs synthesis and asserts the normal `audio_path` contract is
still present; `synthesis_ms` must be a non-negative integer.

- [x] **Step 2: Run that test to verify it fails before implementation**

Run: `& .\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q`

Expected: FAIL because the response does not contain `synthesis_ms`.

- [x] **Step 3: Implement timing and update the README**

Document that Gwen is prewarmed only for cloud backends, that the first response
after opening the GUI may be slower, and that the GUI returns the GPU to local
Llama before local inference. Add this repeatable warm timing probe:

```powershell
$body = @{ text = 'Em chao anh, em da san sang roi.'; fast_mode = $true } | ConvertTo-Json
1..5 | ForEach-Object { Measure-Command { Invoke-RestMethod http://127.0.0.1:5051/tts -Method Post -ContentType 'application/json' -Body $body } }
```

- [x] **Step 4: Run focused verification**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_tts_chunks.py tests\test_gui.py -q
& .\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q
& .\.venv\Scripts\python.exe -m compileall -q src\mini_gpt gwen_tts_server.py
git diff --check
```

Expected: all selected tests pass, compilation returns exit code 0, and no
whitespace errors are reported.

- [ ] **Step 5: Run the live acceptance check**

With the cloud backend and Gwen TTS selected, send five short Vietnamese
replies through the GUI. Record the duration from completed reply text to first
audible playback. Accept only if every warm trial is below two seconds. Then
switch to `GGUF (default)` and confirm `nvidia-smi` shows the Gwen process no
longer owns GPU memory; send one local reply and one short English phrase to
confirm playback and VTube lip-sync.

- [x] **Step 6: Commit verification-facing changes**

```powershell
git add -- README.md gwen_tts_server.py tests/test_gwen_tts_server.py
git commit -m "docs: document warm Gwen speech workflow"
```
