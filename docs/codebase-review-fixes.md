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

---

# Round 3 — Full codebase review & fixes (2026-08-01)

## Summary

A full 7-group parallel review of the whole `D:\LLM` codebase (≈15,300 lines, 100 Python files) found 11 CRITICAL, 46 HIGH, 129 MEDIUM, and 121 LOW issues. All CRITICAL and HIGH issues plus the actionable MEDIUM/LOW ones were fixed across ~40 source files, ~20 test files, scripts, and root files. This round also removed the dead Sherpa-ONNX speech path and rewrote the dead tests that referenced it.

## Fixes by group

### Core runtime (`llama3_1_runtime.py`, `llama_cpp_runtime.py`, `openai_runtime.py`, `llama3_1_training.py`, `gpu_lifecycle.py`, `chat.py`, `reply_orchestration.py`)

- `llama3_1_runtime.py` — model loading moved **outside** `_CACHE_LOCK` (double-checked locking + dedicated `_LOAD_LOCK`); per-model `_MODEL_LOCKS` serialize `generate()`; `clear_llama_generation_cache` skips models in use; CUDA OOM retry with halved `max_new_tokens`; CPU fallback when CUDA missing; `cache_implementation="quantized"` fallback to default cache; bounded evictions per load.
- `llama_cpp_runtime.py` — thread-safe `ensure_started`/`stop` under one lock; `stop()` no longer masks the original error; `--n-gpu-layers` fallback chain; named constants; bounded `error.read()`.
- `openai_runtime.py` — parameter validation before API calls; transient network retry (2×); SSE-only response guard; incremental delta handling kept as the single path; errors never leak raw payloads.
- `llama3_1_training.py` — VRAM-cap abort still writes a manifest (`aborted_reason`); `json.loads` guarded with file/line context; `training_steps` counts real optimizer steps; atomic adapter-save and manifest writes; OOM cleanup; periodic monitor checks.
- `gpu_lifecycle.py` — voice warmup happens **before** releasing the local model (rollback on warm failure); `/release` result is checked.
- `chat.py` — NFC normalization + length bounds.
- `reply_orchestration.py` — `[SEARCH:...]` stripped when search is off; grounded-follow-up failure falls back to the initial response; untrusted web content sanitized (HTML stripped, nested markers removed, per-result and total budget).

### GUI (`gui.py`, `gui_presentation.py`, `scripts/gui_ru_llama3_1.py`)

- `gui.py` — shutdown now sets `_shutting_down`, joins the worker before `release_gpu()`, guards `root.after` calls, releases GPU under `gpu_transition_lock`; `_play_tts` reads `StringVar`s on the main thread before spawning TTS; VTS triggers run in a background thread; streaming is O(n) delta-insert with a text anchor; dictation typed during generation is preserved (only the sent prefix is removed); recording `after` ids are cancelled; TTS generations are keyed and stale chunks are skipped; `_clear_saved_memory` and retrieval share one `_memory_lock`; settings saves debounced; API-key errors are logged; `pygame.mixer` init/quit guarded; narrow exception scopes; `gpu_transition_lock` no longer held across the whole network call.
- `gui_presentation.py` — `append_stream_text` handles cumulative snapshots without duplicating; `safe_streamed_response` uses `lower()`.
- `scripts/gui_ru_llama3_1.py` — input validation, bounded history, JSON error responses, JS escaping, env-based paths.

### Voice / TTS (`speech_runtime.py`, `voice_runtime.py`, `voice_pacing.py`, `tts_chunks.py`, `tts_benchmark.py`, `omnivoice_tuning.py`, `omnivoice_tts_server.py`)

- `speech_runtime.py` — Google recognizer timeout (10 s); narrow exception mapping (`SpeechNoMatchError` / `SpeechServiceError`); unknown engine raises `SpeechConfigurationError`; typed `sr.Recognizer`.
- `voice_runtime.py` — host/port/env paths from env vars with defaults; missing `omnivoice_env` raises a clear error.
- `tts_chunks.py` — sentence regex no longer splits decimals; long words break at the nearest space.
- `tts_benchmark.py` — engine release only when self-loaded; per-sample error logging; sanitized utterance ids; named constants.
- `omnivoice_tts_server.py` — `/tts` returns raw WAV bytes (temp files deleted in `finally`); `/tts_stream` concatenates chunks into one valid WAV via the `wave` module and honors `speed`; text length cap (10k); generic client errors; lazy librosa/torch; numpy/tensor handling; `_MODEL_LOCK` guards model lifecycle; `/status` no longer leaks the pid; lazy `reference_text.txt` read.
- `omnivoice_tuning.py` — added `get_gui_trial_settings()` getter used by the server.
- `gui.py` TTS client — `request_tts_audio` returns `bytes`; `play_wav_blocking` plays from a buffer; the chunk consumer handles both `bytes` and legacy `str` paths.

### VTS (`vts_manager.py`, `vts_motion.py`, `vts_actions.py`, `vts_catalog.py`)

- `vts_manager.py` — request failures mark the connection dead and schedule bounded auto-reconnect (5 attempts, 5 s apart); `shutdown` cancels the sway task and timers; reconnect restarts the sway task; background tasks tracked in a set (no GC races); micro-actions/poses run as background tasks instead of blocking the sway loop; catalog cached with a 10 s TTL; injection pause is serialized through a task chain; `ctypes` cursor reads with pyautogui fallback; token path absolute.
- `vts_motion.py` — eye/head scales clamped to registered parameter bounds; cursor feed clamped; smooth easing; named constants.
- `vts_actions.py` — negation guard (`đừng/không/chớ`) prevents false positive action triggers.
- `vts_catalog.py` — all `[[VTS:...]]` markers parsed (not just the last); bounded catalog instruction.

### Memory / Infra (`memory.py`, `memory_extraction.py`, `conversation_context.py`, `conversation_store.py`, `credential_store.py`, `search.py`, `_atomic_write.py`)

- `memory.py` — CRITICAL `close()` AttributeError fixed (`_db_conn`); migration under `_database_lock`; atomic FAISS/JSON saves; corrupt index rebuilt instead of crashing startup; `MemoryStore` guarded by an RLock; `mmap_size` corrected (30 GB → 300 MB); unused FTS writes removed; TOCTOU supersede check moved inside the transaction; named weights; `clear()` synchronized; embedding model released in `close()`; dedup before add.
- `memory_extraction.py` — sensitive check runs **after** metadata stripping (VTS metadata no longer silently drops memories); decision length bounded; consistent Vietnamese wording.
- `conversation_context.py` — oversized newest turns are truncated instead of dropped.
- `conversation_store.py` — `read_all` under lock; malformed-line logging.
- `credential_store.py` — exceptions logged with root cause; `migrate_legacy` reports its real state.
- `search.py` — per-call executor (no permanently wedged pool); results sanitized and bounded.
- `_atomic_write.py` — unreadable existing JSONL raises instead of being overwritten; fsync before replace; mode preserved; fd leak guard; in-process writer lock.

### Corpus (`_base_corpus.py`, `_corpus_quality.py`, `_safety_patterns.py`, `ru_style.py`, all `ru_v6*` modules, evaluations)

- Dead code removed (`conversation_text`, `prompt_key`, `validate_messages`, `_parts`, duplicated patterns); duplicate regexes merged; `_safety_patterns` is the single source (GRAPHIC/AI_DISCLAIMER vocabulary extended; `tớ` added).
- `ru_style.py` — `RU_SYSTEM_PROMPT` and `ru_system_prompt_for("vi")` unified into one source of truth.
- `approved_v6_1_import.py` — schema-compatible rows + a documented "approved" quality-gate override.
- `ru_v6_2_corpus.py` — now imports `canonical_rows`/`pair_key`/`is_contextual`/`write_jsonl`/`write_splits_atomic` from `_base_corpus` and all patterns from `_safety_patterns` (was the last file still copying them).
- `ru_v6_2_fresh_import.py` — duplicate regex pairs merged; manifest updates include mode/language/turn_band counts; constants + atomic manifest write.
- `ru_v6_3_dialogues.py` — index-out-of-range guards via `% len(...)`; "with him" → "with me"; EN rows now cover repair/bilingual/context_recall (30 rows, quota preserved).
- `ru_v6_3_corpus.py` — turn-band ↔ user-turn-count validation (short=2/medium=3/long=4).
- `ru_hard_evaluation.py` / `ru_llama_evaluation.py` — named score constants; stratum validation; language-aware system prompts for the flirt suite.

### Tests / Scripts / Root

- Dead tests rewritten for the current source: `test_speech_runtime.py` (Google-only), `test_benchmark_stt.py` (Google engine), `test_gui_speech_settings.py` (rejects removed sherpa engine, round-trips Google).
- `run_tests.py` — real exit code, project-root-relative, configurable timeout.
- `test_memory.py` — session-scoped shared store (embedding model loaded once).
- Hardcoded `D:/LLM` paths replaced with `PROJECT_ROOT`-derived paths.
- New coverage: credential round-trips, VTS catalog parsing edge cases, voice pacing markers, TTS benchmark error paths, reply orchestration unclosed markers, invalid backend selection.
- `scripts/build_ru_llama_v6_1_data.py` — audit prefix corrected (`ru-v6-1-audit-`).
- `scripts/evaluate_ru_llama3_1_qlora.py` — per-case try/except + GPU cache cleanup.
- `scripts/build_ru_v6_gguf.py` — temp F16 GGUF always cleaned up (finally), including on failure/KeyboardInterrupt.
- `scripts/chat_ru_llama3_1.py` — bounded history; `scripts/download_llama3_1_hf.py` — removed deprecated `local_dir_use_symlinks`.
- `test_inject.py` — deletes the `LLM_Sway` custom parameter after the test.

## Breaking behavior changes

- **Sherpa-ONNX voice input removed.** `SPEECH_ENGINE_LABELS` only exposes Google; legacy settings storing another engine now raise `SpeechConfigurationError` and the GUI falls back to Google. The `speech-offline` extra was dropped from `pyproject.toml`.
- **OmniVoice `/tts` returns audio bytes** (inline WAV) instead of a temp-file path; the GUI plays from memory and the server deletes its own temp files.
- **`test_benchmark_stt.py` / `test_speech_runtime.py` / `test_gui_speech_settings.py`** no longer need to be ignored — the previously "pre-existing" failures are fixed at the root.

## Verification Instructions

```powershell
cd D:\LLM
.venv\Scripts\python.exe -m pytest tests/ -q --tb=short
.venv\Scripts\python.exe -m py_compile src/mini_gpt/*.py scripts/*.py omnivoice_tts_server.py run_tests.py test_inject.py
```

## Round 4 (2026-08-01): Corpus pipeline refactor

- `scripts/build_corpus.py` — CLI hợp nhất build mọi version (`--version {v6,v6_1,v6_2,v6_2_fresh,v6_3}`), thay thế 5 script `build_ru_llama_v6*_data.py` (đã xóa).
- `src/mini_gpt/corpus_common.py` — machinery dùng chung v6.1/v6.2 (Scene/SCENES, times, stances, acks/details/questions, slots, record, history, final_prompt).
- `src/mini_gpt/corpus_validate.py` — validator tham số hóa thay `_normalize_row`/`build_*_corpus` trùng ~90% của v6.1/v6.2; hai file corpus thành thin wrappers.
- `scripts/verify_corpus_byte_identical.py` — gate SHA256 toàn bộ version so data hiện có (byte-for-byte).
