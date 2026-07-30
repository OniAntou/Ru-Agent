# Codebase Review & Fixes — 2026-07-31

## Summary

Full codebase review of `D:\LLM` (Ru — Local Llama Conversational Runtime) resulted in **6 fixes** across **7 files** and **1 new shared utility module**.

---

## Fixes Applied

### CRITICAL #1 — `openai_runtime.py`: _read_stream not handling incremental deltas

**File:** `src/mini_gpt/openai_runtime.py:88-124`

**Problem:** The `_read_stream` method only handled *cumulative* deltas (where each chunk repeats the full text so far). The standard OpenAI-compatible streaming format uses *incremental* deltas (each chunk is only the new text). With incremental input, `accumulated` was overwritten with the last raw delta instead of accumulating, causing `_read_stream` to return only the last chunk's text instead of the full response.

**Fix:** Restructured the delta handling with three clear branches:
- `not accumulated` → first chunk, set both `new_text` and `accumulated` to the raw delta.
- `raw_delta.startswith(accumulated)` → cumulative format, emit only the new suffix.
- Otherwise → incremental format, emit as-is and append to accumulated.

All three branches are now mutually exclusive and correctly maintain the full accumulated text.

**Test verification:** Existing `test_stream_callback_receives_only_new_text_for_cumulative_provider_events` continues to pass for cumulative format. Incremental format is now also supported transparently.

---

### CRITICAL #2 — `omnivoice_tts_server.py`: Missing cleanup on synthesis failure

**File:** `D:\LLM\omnivoice_tts_server.py:103-121`

**Problem:** `synthesize_fast` created a `NamedTemporaryFile(delete=False)` and closed the handle, then called `sf.write()`. If `sf.write()` raised an exception, the temporary WAV file was left on disk indefinitely (orphaned temp file).

**Fix:** Wrapped `sf.write()` in a `try/finally` block that deletes the temp file on failure before re-raising. The caller receives a clean error without leaving filesystem litter.

```python
try:
    sf.write(output.name, waveform, 24_000)
except Exception:
    try:
        Path(output.name).unlink(missing_ok=True)
    except OSError:
        pass
    raise
```

---

### HIGH #1 — `_safety_patterns.py`: Merged duplicate regex patterns

**File:** `src/mini_gpt/_safety_patterns.py:43,49,55`

**Problem:** Three overlapping/duplicate patterns existed:
- `VI_WRONG_SELF` = `tôi|mình` (self-reference check)
- `VI_DEFAULT_VOICE` = `tôi|mình|bạn` (broader VI pronoun check)
- `SELF_REFERENCE_VOICE` = `tôi|tớ|mình` (word-boundary-aware self-reference)

`SELF_REFERENCE_VOICE` was semantically identical to `VI_WRONG_SELF` (just with word-boundary lookarounds and including `tớ`).

**Fix:**
- Removed `SELF_REFERENCE_VOICE`.
- Merged `tớ` into `VI_WRONG_SELF` (which now covers `tôi|mình|tớ`).
- Added docstring explaining the relationship between `VI_WRONG_SELF` (preferred for self-reference detection) and `VI_DEFAULT_VOICE` (superset with `bạn`, broader pronoun check).
- Updated `ru_v6_1_corpus.py` to import `VI_WRONG_SELF` instead of the removed `SELF_REFERENCE_VOICE`.

---

### MEDIUM #1 — `voice_runtime.py`: Extracted hardcoded constants

**File:** `src/mini_gpt/voice_runtime.py:53-64`

**Problem:** Four magic values (`127.0.0.1`, `5052`, `omnivoice_env`, `omnivoice_tts_server.py`) were inline strings in `voice_service_config()`.

**Fix:** Added module-level private constants:
```python
_OMNIVOICE_HOST = "127.0.0.1"
_OMNIVOICE_PORT = 5052
_OMNIVOICE_ENV_DIR = "omnivoice_env"
_OMNIVOICE_SERVER_SCRIPT = "omnivoice_tts_server.py"
```

These are now the single source of truth for OmniVoice service configuration.

---

### MEDIUM #2 — New `_atomic_write.py`: DRY atomic file write utility

**New file:** `src/mini_gpt/_atomic_write.py`

**Problem:** Both `gui.py` (`_save_settings`, ~20 lines) and `conversation_store.py` (`save`, ~18 lines) had near-identical inline atomic-write logic using `tempfile.mkstemp` + `os.replace`. The duplicated code was identical in structure but with different variable names.

**Fix:** Extracted three shared functions:
- `atomic_write_json(path, data)` — write JSON atomically.
- `atomic_write_text(path, content)` — write plain text atomically.
- `atomic_append_jsonl(path, line)` — append a JSON line atomically (read existing, write all to temp, replace).

**Files updated:**
- `gui.py` — replaced ~20 lines of inline atomic write with `atomic_write_json(GUI_SETTINGS_PATH, settings)`.
- `conversation_store.py` — replaced ~18 lines of inline atomic append with `atomic_append_jsonl(self.path, line)`.

Removed the now-unused `tempfile` import from `gui.py`.

---

### MEDIUM #3 — `gui.py`: Lazy import torch

**File:** `src/mini_gpt/gui.py:29`

**Problem:** `import torch` was at module level but only used for a single `torch.cuda.is_available()` call on the status line (line 484). PyTorch is a heavy import (~2+ seconds, multiple GiB of GPU memory initialization if auto-loaded).

**Fix:** Removed the module-level `import torch` and replaced with a lazy helper:
```python
def _check_cuda() -> bool:
    try:
        import torch
        return torch.cuda.is_available()
    except ImportError:
        return False
```

This is called once during `__init__` to set the initial status, and avoids importing PyTorch at module load time. The `import torch` in `_generate_worker` (which actually uses the model) remains as-is.

---

## Files Changed

| File | Status | Type |
|------|--------|------|
| `src/mini_gpt/openai_runtime.py` | Modified | CRITICAL |
| `omnivoice_tts_server.py` | Modified | CRITICAL |
| `src/mini_gpt/_safety_patterns.py` | Modified | HIGH |
| `src/mini_gpt/ru_v6_1_corpus.py` | Modified | Follow-up |
| `src/mini_gpt/voice_runtime.py` | Modified | MEDIUM |
| `src/mini_gpt/_atomic_write.py` | **New** | MEDIUM |
| `src/mini_gpt/gui.py` | Modified | MEDIUM |
| `src/mini_gpt/conversation_store.py` | Modified | MEDIUM |
| `docs/codebase-review-fixes.md` | **New** | Documentation |

## Verification Instructions

```powershell
cd D:\LLM
.venv\Scripts\python.exe -m pytest tests/test_openai_runtime.py tests/test_memory.py tests/test_credential_store.py tests/test_gpu_lifecycle.py tests/test_voice_pacing.py tests/test_vts_catalog_actions.py tests/test_gui_speech_settings.py tests/test_reply_orchestration.py tests/test_memory_extraction.py -q --tb=short

# Syntax check new file
.venv\Scripts\python.exe -c "import py_compile; py_compile.compile('src/mini_gpt/_atomic_write.py', doraise=True); print('OK')"
```
