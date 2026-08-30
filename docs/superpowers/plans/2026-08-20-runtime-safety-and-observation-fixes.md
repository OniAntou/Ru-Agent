# Runtime Safety and Observation Fixes Implementation Plan

> **Status:** Historical implementation record. The remediation shipped;
> current ownership and privacy boundaries live in
> `docs/operations/runtime-behavior.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the six review findings while preserving Ru's PC-control behavior and current ASP.NET Core architecture.

**Architecture:** Keep PC markers in the execution pipeline, but centralize their removal at display, voice, history, and memory boundaries. Route screen reactions from the Python observer to the WinUI host as acknowledged reaction events, where the existing voice playback pipeline owns audio, subtitles, and VTS state.

**Tech Stack:** Python 3.13, pytest, ASP.NET Core/.NET 10, xUnit, WinUI 3, Gemini REST API, VTube Studio WebSocket.

## Global Constraints

- Preserve unrelated dirty changes already present on `main`.
- Do not reintroduce FastAPI, Uvicorn, or a public Python HTTP server.
- PC actions remain allowlisted and executable; action markers must not become conversational memory metadata.
- Run heavyweight Python, .NET, and build gates sequentially.
- Do not claim live Gemini, audio, or VTube Studio acceptance without user-run smoke evidence.

---

### Task 1: Strict endpoint credential boundary

**Files:**
- Modify: `src/ru_runtime/generation/openai.py`
- Test: `tests/test_openai_runtime.py`

- [x] Add tests proving exact loopback URLs work without a key and remote URLs containing `1234`, `localhost`, or `11434` still require a key.
- [x] Implement URL parsing with exact loopback hostname matching for both model discovery and generation.
- [x] Run `pytest tests/test_openai_runtime.py -q`.

### Task 2: Action-marker memory hygiene

**Files:**
- Modify: `backend-dotnet/RuBackend/Memory/MemoryPolicy.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Memory/MemoryPolicyTests.cs`
- Modify: `src/ru_runtime/memory/extraction.py`
- Test: `tests/test_memory_extraction.py`

- [x] Add regression cases for `SEARCH`, `APP_OPEN`, and `VOLUME` markers in C# and Python memory extraction.
- [x] Strip complete private markers before memory extraction and reject sensitive marker text when it remains.
- [x] Verify action execution code remains unchanged and allowlisted.

### Task 3: Observation event and voice integration

**Files:**
- Modify: `src/ru_runtime/observation/observer_loop.py`
- Modify: `src/ru_runtime/worker/v1/observation.py`
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `gui-winui3/Services/ApiService.cs`
- Modify: `gui-winui3/Services/VoicePlaybackService.cs`
- Modify: `gui-winui3/MainWindow.xaml.cs`
- Test: `tests/test_observation_bounds.py`, `tests/test_gemini_vision_observation.py`, and focused WinUI tests where applicable

- [x] Add real observer counters, reaction IDs, timestamps, and stop-state reporting.
- [x] Remove direct worker-side `winsound` playback; publish reactions for the existing WinUI polling path.
- [x] Make the WinUI observer consumer call non-interrupting `VoicePlaybackService` and acknowledge only successfully played events.
- [x] Ensure a chat/reply in progress cannot be interrupted by an observation reaction.
- [x] Run focused Python and WinUI tests.

### Task 4: VTS hotkey correctness

**Files:**
- Modify: `src/ru_runtime/vts/hotkey_mapping.py`
- Modify: `src/ru_runtime/vts/manager.py`
- Modify: `tests/test_vts_hotkey_mapping.py`

- [x] Add missing-primary and missing-reset tests.
- [x] Remove alias fallback and validate custom mappings against the available catalog.
- [x] Verify `_trigger_action` returns false when no catalog hotkey exists.

### Task 5: Gemini model-compatible request payloads

**Files:**
- Modify: `src/ru_runtime/observation/gemini_vision.py`
- Modify: `tests/test_gemini_vision_observation.py`

- [x] Capture request URL, headers, and JSON in tests.
- [x] Keep the API key in `x-goog-api-key` and omit deprecated sampling fields for Gemini 3.6/3.7 model IDs.
- [x] Remove the unverified fallback alias if it is not part of the configured model cascade.

### Task 6: Documentation and full verification

**Files:**
- Modify: `README.md`
- Modify: `docs/operations/runtime-behavior.md`
- Verify against the current cloud-first runtime contract in
  `docs/operations/runtime-behavior.md`

- [x] Align runtime-default and screen-observation documentation with the current implementation.
- [x] Mark completed cloud-first plan work accurately without rewriting historical evidence.
- [x] Run all Python, .NET, WinUI, route, dependency, compile, and build gates.
- [x] Confirm the dirty worktree contains only intended changes and no FastAPI production references.
