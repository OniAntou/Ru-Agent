# Runtime Hardening and Cloud-Memory Defaults Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the reviewed backend, GUI, native-build, streaming, and tooling defects while making cloud-memory sharing default to `true`.

**Architecture:** Keep the current public APIs stable where possible. Add explicit memory-queue invalidation and shutdown ownership, pass canonical stream-reset callbacks through the hosted runtime, and make GUI services asynchronously disposable. Keep cloud-memory sharing user-configurable, with `true` as the default and a test proving `false` prevents hosted memory extraction.

**Tech Stack:** Python 3.13, FastAPI, pytest, httpx, WinUI 3/C#/.NET 10, CMake, vcpkg, GitHub Actions, PowerShell.

---

### Task 1: Lock the requested cloud-memory behavior and regression tests

**Files:**
- Modify: `tests/test_server_api.py`
- Modify: `tests/test_backend_core.py`
- Modify: `tests/test_headless_runner.py`
- Modify: `tests/test_openai_runtime.py`
- Modify: `src/mini_gpt/_server_shared.py`
- Modify: `src/mini_gpt/routers/settings.py`
- Modify: `src/mini_gpt/backend_core.py`
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `gui-winui3/Views/SettingsView.xaml`

- [ ] Add tests that assert the default settings endpoint returns `share_memory_with_cloud` and `cloud_memory_consent` as `true`.
- [ ] Add a test that a hosted backend with cloud sharing disabled does not call the hosted fact extractor.
- [ ] Add a test that the backend core default permits memory context when the caller omits the flag.
- [ ] Add a streaming retry test that emits a reset after a partial first attempt.
- [ ] Run the focused tests and confirm the new expectations fail before production changes.
- [ ] Change Python, C#, and XAML defaults to `true`; preserve explicit `false` settings.
- [ ] Run the focused tests again and confirm they pass.

### Task 2: Make self-learning queue clearing and shutdown safe

**Files:**
- Modify: `src/mini_gpt/_server_shared.py`
- Modify: `src/mini_gpt/routers/status.py`
- Modify: `src/mini_gpt/routers/voice.py`
- Modify: `tests/test_server_api.py`
- Modify: `tests/test_vts_api.py`

- [ ] Add a queue-generation test proving items queued before memory clear are ignored afterward.
- [ ] Add a shutdown test proving the core is not closed while the learner thread is still active.
- [ ] Add a voice serialization test proving TTS cannot overlap a memory extraction.
- [ ] Implement queue generation invalidation, queue draining, `task_done`, and a shutdown sentinel.
- [ ] Wrap local fact extraction in the existing GPU lifecycle and wait for the learner before closing the core.
- [ ] Make voice TTS and warmup acquire the memory-turn ownership lock with a bounded response when busy.
- [ ] Run the focused tests and then the complete Python suite.

### Task 3: Fix path validation and hosted retry stream consistency

**Files:**
- Modify: `src/mini_gpt/headless_runner.py`
- Modify: `src/mini_gpt/reply_orchestration.py`
- Modify: `src/mini_gpt/openai_runtime.py`
- Modify: `tests/test_headless_runner.py`
- Modify: `tests/test_openai_runtime.py`

- [ ] Add a sibling-prefix path test using `models-evil` that must be rejected.
- [ ] Add a partial-stream network-failure test that expects one canonical reset and retry deltas.
- [ ] Replace string-prefix validation with `Path.is_relative_to`.
- [ ] Thread `on_stream_reset` through orchestration and the OpenAI runtime; reset before retrying after any emitted partial content.
- [ ] Run the focused tests and complete Python suite.

### Task 4: Close GUI services and preserve diagnostics

**Files:**
- Modify: `gui-winui3/MainWindow.xaml.cs`
- Modify: `gui-winui3/Services/ApiService.cs`
- Modify: `gui-winui3/Services/ScreenObserverService.cs`
- Modify: `gui-winui3/Services/VoicePlaybackService.cs`
- Modify: `gui-winui3/Services/BackendProcessManager.cs`
- Modify: `gui-winui3/Views/SettingsView.xaml.cs`
- Modify: `gui-winui3/Views/LogView.xaml.cs`

- [ ] Add testable disposal/state helpers where existing GUI test infrastructure permits; otherwise verify the lifecycle through compilation and static call-path inspection.
- [ ] Stop timers, unsubscribe observers, stop observation, dispose voice/API/WebSocket services, and only then terminate the backend process.
- [ ] Reset VTS speaking state on interrupted playback and honor the `interrupt` argument.
- [ ] Fail backend startup if the Job Object cannot be configured or the child cannot be assigned.
- [ ] Persist child stdout/stderr to the documented `logs/server_out.log` and `logs/server_err.log` files.
- [ ] Remove model-search text changes from settings autosave and coalesce changes that arrive during an in-flight save.
- [ ] Build Debug and Release WinUI targets.

### Task 5: Repair native build reproducibility and obsolete OmniVoice tooling

**Files:**
- Modify: `scripts/build_ru_desktop_overlay.ps1`
- Modify: `.github/workflows/ci.yml`
- Modify: `scripts/setup_omnivoice_env.ps1`
- Modify: `scripts/run_omnivoice_benchmark.ps1`
- Modify: `README.md`
- Modify: `TODO.md`

- [ ] Add clear stale-build/toolchain diagnostics and ensure the native script configures from the manifest toolchain every time.
- [ ] Add a Windows CI native-overlay job using the checked-in vcpkg manifest.
- [ ] Replace deleted Gwen setup/benchmark dependencies with the active `.venv` and OmniVoice-only benchmark flow.
- [ ] Document that cloud-memory sharing defaults to enabled and can be disabled explicitly.
- [ ] Mark remaining model quality-gate and manual voice/VTS checks as release gates rather than implying full acceptance.
- [ ] Run OmniVoice and native scripts in non-mutating preflight mode where available.

### Task 6: Final verification and documentation audit

**Files:**
- Review: `README.md`, `TODO.md`, `docs/superpowers/plans/2026-08-07-runtime-hardening.md`

- [ ] Run `pytest -q`, `compileall`, `pip check`, corpus verification, `dotnet build` Debug/Release, and native CTest/build checks.
- [ ] Run `git diff --check` and inspect all changed files for unrelated modifications.
- [ ] Report any checks blocked by missing external dependencies instead of calling them passed.
- [ ] Re-scan project documents for stale claims and update only documents affected by the implemented behavior.
