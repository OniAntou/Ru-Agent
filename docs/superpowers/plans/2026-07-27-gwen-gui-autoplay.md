# Gwen GUI Autoplay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically speak each completed Ru reply with Gwen while sharing the 6 GiB GPU safely, then remove all F5 artifacts.

**Architecture:** A lazy local Flask service on port 5051 uses the isolated `gwen_env` and releases its model after each request. The GUI clears the cached Llama model before it calls the service, then retains its current pygame playback and cleanup flow.

**Tech Stack:** Python 3.11, Flask, Qwen-TTS, PyTorch CUDA, Tkinter, pytest, pygame.

---

### Task 1: Cover the provider contract with tests

**Files:**
- Create: `tests/test_gwen_tts_server.py`
- Modify: `tests/test_gui.py`

- [ ] **Step 1: Write failing service-validation tests**

```python
from gwen_tts_server import create_app

def test_tts_rejects_missing_text():
    response = create_app(model_loader=lambda: None).test_client().post("/tts", json={})
    assert response.status_code == 400
    assert response.get_json() == {"error": "No text provided"}

def test_tts_returns_synthesized_path(tmp_path):
    wav = tmp_path / "gwen.wav"
    wav.write_bytes(b"RIFF")
    app = create_app(model_loader=lambda: object(), synthesize=lambda _model, _text: str(wav))
    assert app.test_client().post("/tts", json={"text": "hello"}).get_json() == {"audio_path": str(wav)}
```

- [ ] **Step 2: Write a failing GUI test that asserts the Gwen endpoint and Llama release occur before the request**

```python
monkeypatch.setattr(gui, "clear_llama_generation_cache", lambda: calls.append("clear"))
monkeypatch.setattr(gui, "request_tts_audio", lambda _: calls.append("request") or str(wav))
app._play_tts("hello")
assert calls[:2] == ["clear", "request"]
```

- [ ] **Step 3: Run the tests to prove the current code fails**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py tests\test_gui.py -q`

Expected: missing Gwen module and/or current F5 endpoint assertion failure.

- [ ] **Step 4: Commit tests**

```powershell
git add tests/test_gwen_tts_server.py tests/test_gui.py
git commit -m "test: cover Gwen TTS integration"
```

### Task 2: Implement the lazy Gwen service

**Files:**
- Create: `gwen_tts_server.py`
- Create: `run_gwen_tts_server.bat`
- Modify: `tests/test_gwen_tts_server.py`

- [ ] **Step 1: Implement a Flask factory with the current audio-path contract**

```python
def create_app(model_loader=None, synthesize=None) -> Flask:
    app = Flask(__name__)
    @app.post("/tts")
    def tts():
        text = (request.get_json(silent=True) or {}).get("text", "").strip()
        if not text:
            return jsonify({"error": "No text provided"}), 400
        model = None
        try:
            model = (model_loader or load_gwen_model)()
            path = (synthesize or synthesize_gwen)(model, text)
            return jsonify({"audio_path": path})
        except Exception as error:
            return jsonify({"error": str(error) or type(error).__name__}), 500
        finally:
            release_model(model)
    return app
```

- [ ] **Step 2: Implement local synthesis and explicit release**

```python
def load_gwen_model():
    return Qwen3TTSModel.from_pretrained(str(MODEL_DIR), device_map="cuda:0", dtype=torch.bfloat16, attn_implementation="sdpa")

def synthesize_gwen(model, text: str) -> str:
    ref_text = REFERENCE_TEXT.read_text(encoding="utf-8").strip()
    wavs, sample_rate = model.generate_voice_clone(text=text, language="Vietnamese", ref_audio=str(REFERENCE_AUDIO), ref_text=ref_text, non_streaming_mode=True)
    output = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    output.close()
    sf.write(output.name, wavs[0], sample_rate)
    return output.name

def release_model(model) -> None:
    del model
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
```

- [ ] **Step 3: Add a launcher with no environment activation side effect**

```bat
@echo off
setlocal
D:\LLM\gwen_env\Scripts\python.exe D:\LLM\gwen_tts_server.py
```

- [ ] **Step 4: Run provider tests**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q`

Expected: pass without model download or CUDA allocation.

- [ ] **Step 5: Commit provider**

```powershell
git add gwen_tts_server.py run_gwen_tts_server.bat tests/test_gwen_tts_server.py
git commit -m "feat: add lazy Gwen TTS service"
```

### Task 3: Route existing automatic voice playback to Gwen

**Files:**
- Modify: `src/mini_gpt/gui.py:22-61,513-543`
- Modify: `tests/test_gui.py`

- [ ] **Step 1: Replace the endpoint and import cache release**

```python
from .llama3_1_runtime import clear_llama_generation_cache, dependency_status, generate_llama_response_hf
GWEN_TTS_SERVER_URL = "http://127.0.0.1:5051/tts"
```

- [ ] **Step 2: Keep all current response validation but call Gwen with a longer timeout**

```python
response = post(GWEN_TTS_SERVER_URL, json={"text": text}, timeout=90)
```

- [ ] **Step 3: Clear Llama before automatic voice generation**

```python
self.root.after(0, lambda: self.status_var.set("Preparing Gwen voice..."))
clear_llama_generation_cache()
audio_file = request_tts_audio(clean_text)
```

- [ ] **Step 4: Run GUI tests**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q`

Expected: all existing and new endpoint/cache-order tests pass.

- [ ] **Step 5: Commit GUI route**

```powershell
git add src/mini_gpt/gui.py tests/test_gui.py
git commit -m "feat: autoplay Gwen voice in Ru GUI"
```

### Task 4: Smoke-test and remove F5 after Gwen succeeds

**Files:**
- Delete: `f5_tts_server.py`, `run_f5_tts_server.bat`, `src/mini_gpt/tts_capabilities.py`, `tests/test_tts_capabilities.py`
- Delete: `D:\LLM\f5_env`, `C:\Users\USER\.cache\huggingface\hub\models--SWivid--F5-TTS`
- Delete: `D:\LLM\test_f5_chao.wav`, `test_f5_hello.wav`, `test_f5_old.wav`, `test_f5_v1.wav`
- Modify: `README.md`, `TODO.md`, `docs\ANTIGRAVITY_HANDOFF.md`

- [ ] **Step 1: Start the service and request the short Ru greeting**

Run: `D:\LLM\gwen_env\Scripts\python.exe gwen_tts_server.py`

Run: `Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5051/tts -ContentType application/json -Body '{"text":"Chào anh, em là Ru."}'`

Expected: JSON `audio_path` to a non-empty 24 kHz mono WAV.

- [ ] **Step 2: Prove GPU release and later Llama reload**

Run: `nvidia-smi --query-gpu=memory.used --format=csv,noheader`

Expected: memory returns near idle after the Gwen request; a normal later Ru chat can load Llama without OOM.

- [ ] **Step 3: Remove only the verified F5 targets**

```powershell
[System.IO.Directory]::Delete('D:\LLM\f5_env', $true)
[System.IO.Directory]::Delete('C:\Users\USER\.cache\huggingface\hub\models--SWivid--F5-TTS', $true)
Remove-Item -LiteralPath f5_tts_server.py,run_f5_tts_server.bat,src\mini_gpt\tts_capabilities.py,tests\test_tts_capabilities.py,test_f5_chao.wav,test_f5_hello.wav,test_f5_old.wav,test_f5_v1.wav -Force
```

- [ ] **Step 4: Replace F5 documentation with this experimental Gwen guidance**

```markdown
The GUI automatically uses the local Gwen service on port 5051. Start it with
`D:\LLM\run_gwen_tts_server.bat` while testing voice. Gwen releases its model
after each reply to share the 6 GiB GPU with Llama; pronunciation remains under
user evaluation.
```

- [ ] **Step 5: Run final checks and commit**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py tests\test_gui.py -q`

Run: `rg -n "F5|f5" README.md TODO.md docs src tests`

Run: `git diff --check`

Expected: tests pass, no active F5 source or documentation references, and no whitespace errors.

```powershell
git add -u README.md TODO.md docs f5_tts_server.py run_f5_tts_server.bat src tests
git add gwen_tts_server.py run_gwen_tts_server.bat
git commit -m "chore: replace F5 with Gwen voice experiment"
```

