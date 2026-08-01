# Codebase Hardening Implementation Plan

> Status: implemented and verified on 2026-08-01. Manual microphone, playback,
> VTube Studio, and cleanup of processes left unmanaged by older builds remain
> operator checks. New Tauri launches own their complete backend process tree.

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close the reviewed security, lifecycle, correctness, privacy, reproducibility, and documentation gaps without changing Ru's approved GGUF/HF model direction or touching personal data.

**Architecture:** Make the Tauri shell the owner of a single authenticated loopback backend, route all frontend traffic through one authenticated client, and make backend generation operate on request-scoped session context. Keep model, voice, VTS, memory, and training concerns behind narrow lifecycle/provenance interfaces so each contract can be tested independently.

**Tech Stack:** Python 3.11, FastAPI, Pydantic, pytest, React 19, TypeScript, Vitest, Tauri 2/Rust, llama.cpp, SQLite.

---

### Task 1: Establish regression gates and artifact hygiene

**Files:**
- Modify: `.gitignore`, `.gitattributes`, `pyproject.toml`
- Create: `gui-tauri/src/test/setup.ts`
- Modify: `gui-tauri/package.json`, `gui-tauri/package-lock.json`

1. Add test tooling and ignore only generated/private runtime artifacts (`data/memory.db`, installer binaries, caches).
2. Add line-ending policy and focused CI commands.
3. Run the existing Python, frontend, Rust, and overlay baselines sequentially.

### Task 2: Secure and own the desktop runtime

**Files:**
- Test: `tests/test_server_api.py`
- Create: `gui-tauri/src/lib/backendClient.ts`, `gui-tauri/src/lib/backendClient.test.ts`
- Modify: `src/mini_gpt/server.py`, `start_dev.bat`
- Modify: `gui-tauri/src-tauri/src/lib.rs`, `gui-tauri/src-tauri/Cargo.toml`, `gui-tauri/src-tauri/tauri.conf.json`, `gui-tauri/src-tauri/capabilities/default.json`
- Modify: all Tauri components that call `fetch` or `WebSocket`

1. Write failing tests for rejected foreign origins, missing/invalid bearer tokens, authenticated REST/WS calls, and oversized requests.
2. Add exact-origin CORS, constant-time token validation, WebSocket token validation, and bounded request/payload sizes.
3. Write failing Rust/frontend tests for runtime config and reconnect-safe authenticated clients.
4. Make Tauri generate the per-run token, spawn exactly one backend child, expose runtime config, and terminate its complete Windows process tree through a kill-on-close Job Object. Remove opener/template surfaces and set CSP.
5. Verify Python tests, Vitest, frontend build, and cargo checks.

### Task 3: Make chat sessions, streaming, markers, and VTS correct

**Files:**
- Test: `tests/test_server_api.py`, `tests/test_backend_core.py`, `tests/test_headless_runner.py`
- Test: `gui-tauri/src/components/ChatTab.test.tsx`
- Modify: `src/mini_gpt/backend_core.py`, `src/mini_gpt/headless_runner.py`, `src/mini_gpt/server.py`, `src/mini_gpt/vts_manager.py`
- Modify: `gui-tauri/src/components/ChatTab.tsx`

1. Write failing tests proving session B cannot inherit/send to session A and reconnect uses the live socket.
2. Replace global session turns with context derived from the request session under the generation lock.
3. Add stream-reset protocol and replace the provisional stream with the final canonical reply.
4. Fetch the current VTS catalog before generation, inject its private instruction, parse/strip all private markers once before display, persistence, memory, TTS, and learning, then dispatch only validated catalog codes.
5. Add VTS initial-connect retry and truthful manual-action results.
6. Verify focused backend/frontend tests.

### Task 4: Fix memory privacy and resource lifecycles

**Files:**
- Test: `tests/test_backend_core.py`, `tests/test_server_api.py`, `tests/test_memory.py`
- Modify: `src/mini_gpt/backend_core.py`, `src/mini_gpt/memory.py`, `src/mini_gpt/server.py`
- Modify: `gui-tauri/src/components/MemoryTab.tsx`, `gui-tauri/src/components/SettingsTab.tsx`

1. Write failing tests for closing SQLite before deletion, self-learning gating all automatic writes, and cloud memory exclusion by default.
2. Add explicit `share_memory_with_cloud=false`; retrieve/inject memory into hosted models only with consent.
3. Make Clear Memory clear structured memory only and report that session history is retained.
4. Remove unused FAISS/eager sentence-transformer legacy code and dependencies while preserving safe artifact cleanup.
5. Add FastAPI shutdown cleanup for owned voice process, VTS, runner, and memory store.

### Task 5: Coordinate GPU, voice, recording, and playback

**Files:**
- Test: `tests/test_server_api.py`, `tests/test_gpu_lifecycle.py`
- Test: `gui-tauri/src/lib/playVoice.test.ts`, `gui-tauri/src/lib/voiceInput.test.ts`
- Modify: `src/mini_gpt/server.py`, `omnivoice_tts_server.py`
- Modify: `gui-tauri/src/lib/playVoice.ts`, `gui-tauri/src/lib/voiceInput.ts`, chat/voice components

1. Write failing tests for local-generation voice release, speech model release, single-owned voice startup, VTS speaking state, bounded recording, and playback cancellation cleanup.
2. Wire `GpuLifecycle` into generation and TTS/warmup paths.
3. Track and stop only the voice process this backend owns; add service singleton protection to prevent future duplicate heavy models.
4. Bound microphone duration and payload size, cancel on unmount, resolve cancelled playback promises, and revoke object URLs.
5. Verify focused Python and frontend tests.

### Task 6: Validate settings and context budgets

**Files:**
- Test: `tests/test_server_api.py`, `tests/test_conversation_context.py`
- Modify: `src/mini_gpt/server.py`, `src/mini_gpt/conversation_context.py`, `src/mini_gpt/llama_cpp_runtime.py`
- Modify: `gui-tauri/src/components/SettingsTab.tsx`

1. Write failing boundary tests for temperature, token count, top-k, voice speed, prompt, audio, and context capacity.
2. Enforce Pydantic bounds and align UI controls with backend limits.
3. Reserve output tokens from the llama.cpp context budget and truncate memory/history/current input deterministically.
4. Verify focused tests and frontend build.

### Task 7: Repair headless runtime and training reproducibility

**Files:**
- Test: `tests/test_headless_runner.py`, `tests/test_llama3_1_training.py`, corpus reproducibility tests
- Modify: `src/mini_gpt/headless_runner.py`, `src/mini_gpt/llama3_1_training.py`, `scripts/train_ru_llama3_1.py`
- Modify: `configs/ru_llama3_1_v6_3.json`, other active configs if path normalization is needed
- Modify: `scripts/corpus_baseline_hashes.json`, corpus verification scripts/fixtures

1. Write failing tests for Transformers dependency paths and v6.3 config parsing.
2. Restore missing imports and reject unknown config metadata cleanly or model it explicitly.
3. Seed Python, NumPy, and PyTorch; hash train/validation/manifest/base/config inputs; record git/package/runtime provenance in adapter manifests.
4. Require an explicit training config and remove machine-absolute paths from active configs.
5. Make corpus byte-identical verification include all splits and use committed non-private calibration fixtures.
6. Verify all training/corpus tests without starting training or adopting a candidate.

### Task 8: CI, native checks, and documentation truth

**Files:**
- Create: `.github/workflows/ci.yml`
- Modify: `tools/ru_desktop_overlay/build.ps1`, `vcpkg.json`
- Modify: `README.md`, `TODO.md`, `docs/ANTIGRAVITY_HANDOFF.md`, `docs/codebase-review-fixes.md`, `docs/opencodeagentfix.md`, `gui-tauri/README.md`, active specs/plans

1. Run native tests from the build script and pin a vcpkg baseline.
2. Add sequential CI gates for Python, frontend, Rust, corpus reproducibility, and native overlay where supported.
3. Update active docs to match Tauri, authenticated runtime, Kilo/OpenCode, GGUF default with retained HF, privacy, marker, session, voice speed, and GPU lifecycle behavior.
4. Mark historical review documents as archived/superseded and delete only disposable template documentation.
5. Run a stale-document search and ensure no active launch/config guidance points at deleted Tkinter entrypoints.

### Task 9: Full verification and handoff

1. Run `python -m pytest -q` from `.venv`.
2. Run `npm test -- --run` and `npm run build` in `gui-tauri`.
3. Run `cargo test --locked` and `cargo check --locked` sequentially.
4. Run overlay `ctest`, corpus byte-identical verification, `pip check`, and `git diff --check`.
5. Inspect the final scoped diff and repository status; do not stage, commit, push, delete personal data, or kill pre-existing processes.
6. Report any machine-state cleanup that still needs explicit permission separately from completed code fixes.
