# Gwen CPU-resident voice cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the Gwen TTS weights in CPU RAM between requests and loan the GPU to Gwen only while making a WAV, with the current worker process as a fallback.

**Architecture:** `GwenCpuCache` owns one CPU-resident `Qwen3TTSModel`, a lock, and the CUDA transfer lifecycle. The Flask service tries this cache first; on any cache-path failure it clears that session and runs the existing isolated worker, preserving the same `/tts` response. The GUI contract and its existing GGUF-to-voice GPU hand-off stay unchanged.

**Tech Stack:** Python 3.11, Flask, PyTorch CUDA, Qwen TTS, soundfile, pytest.

**Experiment status (2026-07-27):** Not adopted. A real CPU-cache trial created
an abnormally long 1.07 MB WAV and took about 75 seconds for the short test
sentence, compared with about 15 seconds for the existing worker. CUDA memory
returned to the 553 MiB idle baseline after the trial, but its performance and
audio behavior failed the acceptance gate. The uncommitted cache implementation
was removed; the current worker-per-request service remains active.

---

## File map

- `gwen_tts_cache.py`: CPU model loading, temporary CUDA ownership, WAV creation, cleanup, and explicit dependency injection for tests.
- `gwen_tts_server.py`: selects cached synthesis first and preserves the current subprocess worker as a per-request fallback.
- `tests/test_gwen_tts_cache.py`: exercises cache ownership and cleanup without loading Qwen or CUDA.
- `tests/test_gwen_tts_server.py`: verifies cache success and fallback through the existing Flask endpoint.
- `README.md`: explains the warm CPU cache and the unchanged 6 GB GPU safety rule.

### Task 1: Isolate and test the CPU cache lifecycle

**Files:**

- Create: `gwen_tts_cache.py`
- Create: `tests/test_gwen_tts_cache.py`

- [ ] **Step 1: Write failing lifecycle tests with fake Qwen dependencies.**

```python
from gwen_tts_cache import GwenCpuCache


class FakeInnerModel:
    def __init__(self):
        self.devices = []

    def to(self, device):
        self.devices.append(device)


class FakeModel:
    def __init__(self):
        self.model = FakeInnerModel()

    def generate_voice_clone(self, **_kwargs):
        return [[0.1, 0.2]], 24_000


def test_cache_loads_once_moves_to_cuda_then_back_to_cpu(tmp_path):
    model = FakeModel()
    loads = []
    writes = []
    cache = GwenCpuCache(
        model_dir=tmp_path,
        reference_audio=tmp_path / "voice.wav",
        reference_text_path=tmp_path / "voice.txt",
        model_loader=lambda *_args, **_kwargs: loads.append(True) or model,
        write_wav=lambda path, wav, rate: writes.append((path, wav, rate)),
        empty_cuda_cache=lambda: writes.append(("empty",)),
    )
    (tmp_path / "voice.wav").write_bytes(b"RIFF")
    (tmp_path / "voice.txt").write_text("reference", encoding="utf-8")

    assert cache.synthesize("Chào anh", tmp_path / "one.wav") == str(tmp_path / "one.wav")
    assert cache.synthesize("Em là Ru", tmp_path / "two.wav") == str(tmp_path / "two.wav")
    assert loads == [True]
    assert model.model.devices == ["cuda:0", "cpu", "cuda:0", "cpu"]
    assert [entry[0] for entry in writes].count("empty") == 2


def test_cache_returns_model_to_cpu_when_generation_fails(tmp_path):
    class BrokenModel(FakeModel):
        def generate_voice_clone(self, **_kwargs):
            raise RuntimeError("generation failed")

    model = BrokenModel()
    cache = GwenCpuCache.for_test(tmp_path, model)

    with pytest.raises(RuntimeError, match="generation failed"):
        cache.synthesize("Chào anh", tmp_path / "broken.wav")
    assert model.model.devices == ["cuda:0", "cpu"]
```

- [ ] **Step 2: Run the new tests to verify the missing cache module fails.**

Run: `D:\LLM\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_cache.py -q`

Expected: collection fails with `ModuleNotFoundError: No module named 'gwen_tts_cache'`.

- [ ] **Step 3: Implement `GwenCpuCache` with one serialized transfer path.**

```python
class GwenCpuCache:
    def __init__(self, model_dir, reference_audio, reference_text_path, *, model_loader, write_wav, empty_cuda_cache):
        self._model_dir = Path(model_dir)
        self._reference_audio = Path(reference_audio)
        self._reference_text_path = Path(reference_text_path)
        self._model_loader = model_loader
        self._write_wav = write_wav
        self._empty_cuda_cache = empty_cuda_cache
        self._model = None
        self._lock = threading.Lock()

    def synthesize(self, text, output_path):
        with self._lock:
            model = self._load_cpu_model()
            model.model.to("cuda:0")
            try:
                wavs, sample_rate = model.generate_voice_clone(
                    text=text,
                    language="Vietnamese",
                    ref_audio=str(self._reference_audio),
                    ref_text=self._reference_text(),
                    non_streaming_mode=True,
                )
                if not wavs:
                    raise RuntimeError("Gwen returned no audio.")
                self._write_wav(str(output_path), wavs[0], sample_rate)
                return str(output_path)
            finally:
                model.model.to("cpu")
                self._empty_cuda_cache()
```

`_load_cpu_model()` validates the model/reference files, imports `torch` and
`Qwen3TTSModel` lazily, and calls
`Qwen3TTSModel.from_pretrained(..., device_map="cpu", dtype=torch.bfloat16,
attn_implementation="sdpa")` exactly once. `for_test()` constructs the same
object with a supplied model and no real Qwen imports.

- [ ] **Step 4: Run lifecycle tests.**

Run: `D:\LLM\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_cache.py -q`

Expected: `2 passed`.

- [ ] **Step 5: Commit the isolated cache unit.**

```powershell
git add gwen_tts_cache.py tests/test_gwen_tts_cache.py
git commit -m "feat: add CPU-resident Gwen cache"
```

### Task 2: Add a server-level fallback without changing the endpoint contract

**Files:**

- Modify: `gwen_tts_server.py`
- Modify: `tests/test_gwen_tts_server.py`

- [ ] **Step 1: Write endpoint tests for cached synthesis and fallback.**

```python
def test_tts_uses_cached_synthesizer_first(tmp_path):
    output = tmp_path / "cached.wav"
    output.write_bytes(b"RIFF")
    app = create_app(synthesize=lambda _text: str(output))

    response = app.test_client().post("/tts", json={"text": "Chào anh"})

    assert response.status_code == 200
    assert response.get_json() == {"audio_path": str(output)}


def test_tts_falls_back_when_cached_synthesizer_fails(tmp_path):
    output = tmp_path / "fallback.wav"
    output.write_bytes(b"RIFF")
    app = create_app(
        synthesize=lambda _text: (_ for _ in ()).throw(RuntimeError("cache failed")),
        fallback_synthesize=lambda _text: str(output),
    )

    assert app.test_client().post("/tts", json={"text": "Chào anh"}).get_json() == {"audio_path": str(output)}
```

- [ ] **Step 2: Run the server tests to verify `fallback_synthesize` is unsupported.**

Run: `D:\LLM\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q`

Expected: `TypeError` for the unsupported `fallback_synthesize` keyword.

- [ ] **Step 3: Route the service through the cache and preserve worker fallback.**

```python
def create_app(synthesize=None, fallback_synthesize=None) -> Flask:
    cached_synthesize = synthesize or synthesize_cached
    worker_fallback = fallback_synthesize or synthesize_gwen

    @app.post("/tts")
    def tts():
        text = (request.get_json(silent=True) or {}).get("text", "").strip()
        if not text:
            return jsonify({"error": "No text provided"}), 400
        try:
            audio_path = cached_synthesize(text)
        except Exception:
            audio_path = worker_fallback(text)
        if not Path(audio_path).is_file():
            return jsonify({"error": "Gwen did not create a playable audio file."}), 500
        return jsonify({"audio_path": audio_path})
```

`synthesize_cached()` owns a temporary WAV path, delegates to one lazy
`GwenCpuCache`, and deletes its empty path if the cache path raises. It does not
hide a worker fallback failure: Flask returns that error as HTTP 500 just as it
does today.

- [ ] **Step 4: Run server tests.**

Run: `D:\LLM\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_server.py -q`

Expected: all tests pass.

- [ ] **Step 5: Commit server fallback integration.**

```powershell
git add gwen_tts_server.py tests/test_gwen_tts_server.py
git commit -m "feat: fall back from cached Gwen synthesis"
```

### Task 3: Measure the real GPU hand-off and document the experiment

**Files:**

- Modify: `README.md`

- [ ] **Step 1: Add the benchmark command.**

```powershell
$body = @{ text = 'Chào anh, em là Ru.' } | ConvertTo-Json -Compress
1..2 | ForEach-Object {
  $timer = [Diagnostics.Stopwatch]::StartNew()
  $reply = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5051/tts -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 180
  $timer.Stop()
  [pscustomobject]@{ run = $_; seconds = [math]::Round($timer.Elapsed.TotalSeconds, 2); audio_path = $reply.audio_path }
}
```

State that first use loads the CPU cache, later use must be measured locally,
and the current worker remains the automatic fallback. Keep the existing
no-concurrent-GPU and quality-gate wording.

- [ ] **Step 2: Verify the automated suites and run the real benchmark.**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_llama_cpp_runtime.py tests\test_build_ru_v6_gguf.py -q
& D:\LLM\gwen_env\Scripts\python.exe -m pytest tests\test_gwen_tts_cache.py tests\test_gwen_tts_server.py -q
```

Expected: all selected tests pass. Record first and second response-to-WAV
times, then start one GGUF response and verify no CUDA out-of-memory error.

- [ ] **Step 3: Commit documentation and measured outcome.**

```powershell
git add README.md
git commit -m "docs: record Gwen cache benchmark"
```
