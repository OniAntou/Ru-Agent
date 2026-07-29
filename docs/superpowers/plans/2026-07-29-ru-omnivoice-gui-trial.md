# Ru OmniVoice GUI Trial Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Add reversible Ru/Yến Nhi OmniVoice trial choices to the desktop GUI while retaining Gwen as the default and enforcing one local voice engine on the GPU.

**Architecture:** A focused `voice_runtime` module owns the four labels, engine/profile identifiers, legacy migration, and service configuration. `omnivoice_tts_server.py` mirrors Gwen's loopback contract on port 5052 in `omnivoice_env`. A generalized lifecycle releases non-selected voice services before warming the selected one.

**Tech Stack:** Python 3.11, Tkinter, Flask, PyTorch CUDA, OmniVoice, requests, pygame, pytest.

## Global Constraints

- Gwen remains persisted default; unknown/legacy values migrate to `Ru (Gwen)`.
- The exact labels are `Ru (Gwen)`, `Yến Nhi (Gwen)`, `Ru (OmniVoice — test)`, and `Yến Nhi (OmniVoice — test)`.
- OmniVoice uses only `D:\LLM\omnivoice_env\Scripts\python.exe`, `omnivoice_tts_server.py`, and `127.0.0.1:5052`; GUI downloads nothing.
- Reuse only existing Ru/Yến Nhi reference pairs; never add recordings or auto-fallback from a failed selected engine.
- Local Llama, Gwen, and OmniVoice cannot remain CUDA-resident together.
- Preserve the sentence queue, VTS metadata filtering, and `VoiceVolume` lip-sync. Add only focused tests.

---

## File structure

- Create `src/mini_gpt/voice_runtime.py`: selection/model-independent service config.
- Create `tests/test_voice_runtime.py`: four-choice and legacy migration tests.
- Create `omnivoice_tts_server.py`: isolated two-profile Flask service.
- Create `tests/test_omnivoice_tts_server.py`: fake-model service contracts.
- Modify `src/mini_gpt/gpu_lifecycle.py`, `tests/test_gpu_lifecycle.py`: selected-engine handoff.
- Modify `src/mini_gpt/gui.py`; create `tests/test_gui_voice_selection.py`: selector, persistence, service boundary, playback.
- Modify `README.md` and `docs/ANTIGRAVITY_HANDOFF.md`: trial instructions and default status.

## Task 1: Define voice selections and service configuration

**Files:** Create `src/mini_gpt/voice_runtime.py`; create `tests/test_voice_runtime.py`.

**Interfaces:** `VoiceSelection(label: str, engine_id: str, profile_id: str)`; `VoiceServiceConfig(engine_id: str, base_url: str, host: str, port: int, python_path: Path, server_script: Path)`; `VOICE_SELECTIONS`; `DEFAULT_VOICE_SELECTION`; `voice_selection_by_label(label)`; `voice_selection_from_saved(value)`; `voice_service_config(selection, project_root)`.

- [ ] Write tests proving `Yến Nhi (OmniVoice — test)` resolves to `omnivoice`/`yen_nhi`, legacy `Ru (Tsukuyomi)` and unknown values resolve to Gwen/Ru, and selected OmniVoice config is URL `http://127.0.0.1:5052`, Python `omnivoice_env`, script `omnivoice_tts_server.py`.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_voice_runtime.py -q`; expect an import failure for `voice_runtime`.
- [ ] Implement four ordered immutable selections. Map legacy label to Gwen/Ru and make non-string/unknown saved values default. Keep module free of Tkinter, Flask, torch, requests, and GUI imports.
- [ ] Rerun the focused test; expect PASS.
- [ ] Commit `src/mini_gpt/voice_runtime.py` and test with message `feat: define selectable voice runtimes`.

## Task 2: Add isolated OmniVoice service

**Files:** Create `omnivoice_tts_server.py`; create `tests/test_omnivoice_tts_server.py`.

**Interfaces:** `voice_profile(profile) -> tuple[Path, str]`; `warm_model(profile) -> dict[str, bool]`; `release_model()`; `synthesize_fast(text, profile) -> str`; `create_app(synthesize=None) -> Flask`. Endpoints are `GET /status`, `POST /warmup`, `POST /release`, and `POST /tts`, matching Gwen JSON fields including `voice_profile`, `audio_path`, and `synthesis_ms`.

- [ ] Write fake-boundary tests that warm Ru twice and Yến Nhi once, proving one clone prompt per profile; test profile validation, status without loading, release clearing both prompts/model, empty `/tts`, valid WAV response, and synthesis error HTTP 500. Do not import actual OmniVoice/CUDA in tests.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_omnivoice_tts_server.py -q`; expect missing module failure.
- [ ] Implement Flask service like Gwen: set D-drive Hugging Face cache, lazy-import OmniVoice/torch/soundfile, load `k2-fsa/OmniVoice` as CUDA float16, create profile prompt from exact existing reference pairs, generate at `num_step=16`, write 24 kHz temp WAV. Release model/prompts, collect garbage, empty CUDA cache; bind 5052 only in main.
- [ ] Rerun Omni/Gwen service tests; expect PASS without model download.
- [ ] Commit service/tests with `feat: add OmniVoice trial service`.

## Task 3: Generalize GPU service ownership

**Files:** Modify `src/mini_gpt/gpu_lifecycle.py`; modify `tests/test_gpu_lifecycle.py`.

**Interfaces:** constructor accepts `voice_services: Mapping[str, VoiceServiceController]`; controller exposes `available()`, `ensure()`, `request(path, payload)`. `warm_voice_for_cloud(selection, set_status)` releases local Llama and other services, then warms selection engine/profile. `prepare_local_generation()` releases every available service. `prepare_speech(selection)` releases Llama, releases other services, ensures/warm selected service.

- [ ] Write a failing test: warming Yến Nhi/OmniVoice emits Llama release, Gwen `/release`, status `Warming OmniVoice voice...`, then OmniVoice `/warmup` with `{"voice_profile": "yen_nhi"}`. Add Gwen regression and local-generation releases-both test.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_gpu_lifecycle.py -q`; expect Gwen-specific interface failure.
- [ ] Implement controller mapping while preserving lock and elapsed-time behavior. Never expose URL/profile internals in status; never ensure a non-selected service.
- [ ] Rerun lifecycle test; expect PASS.
- [ ] Commit with `feat: coordinate selected voice GPU service`.

## Task 4: Wire GUI selection and service request boundary

**Files:** Modify `src/mini_gpt/gui.py`; create `tests/test_gui_voice_selection.py`.

**Interfaces:** GUI uses `voice_selection_var`, `_selected_voice_selection() -> VoiceSelection`, `ensure_voice_service(selection)`, `request_tts_audio(text, selection, *, post=None) -> str`, and `request_voice_control(selection, path, payload=None, *, post=None)`. Existing `voice_profile` settings key stores selected label only.

- [ ] Write failing isolated tests: legacy `Ru (Tsukuyomi)` displays `Ru (Gwen)`; OmniVoice request POST URL is `http://127.0.0.1:5052/tts` with only text and `voice_profile: ru`; selected Omni service startup uses D `omnivoice_env` and root server script; a selected Omni failure makes no Gwen request; all four labels are present without Tk root.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_gui_voice_selection.py -q`; expect current Gwen-only constants/functions to fail.
- [ ] Replace Gwen-only constants/functions with selection/config helpers. Combobox comes from `VOICE_SELECTIONS`; load/save label through migration; generation warmup and playback pass full selection. Keep detached/no-window startup, existing clean text, playback queue, and `Voice unavailable` error boundary. Never change the saved selection or fallback on a failed service.
- [ ] Run GUI/lifecycle/Omni/Gwen focused tests; expect PASS without services or Tk root.
- [ ] Commit GUI/tests with `feat: expose OmniVoice trial in GUI`.

## Task 5: Document and manually verify the trial

**Files:** Modify `README.md`; modify `docs/ANTIGRAVITY_HANDOFF.md`.

- [ ] Search `rg -n --fixed-strings 'Ru (OmniVoice — test)' README.md docs\ANTIGRAVITY_HANDOFF.md`; confirm new selector instructions are absent before docs change.
- [ ] Document four labels, D-only installed runtime prerequisite, Gwen default, selected-engine-only GPU use, no fallback, and manual test: Ru/Yến Nhi with each engine, setting persistence across restart, local Llama release, and no spoken VTS metadata.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest -q`, `& .\.venv\Scripts\python.exe -m compileall -q src scripts gwen_tts_server.py omnivoice_tts_server.py`, then open GUI and test all two Omni profiles plus switch back to Gwen.
- [ ] Commit docs with `docs: document OmniVoice GUI trial`.

## Plan self-review

- Tasks 1 and 4 implement the four persisted labels and GUI playback boundary; Task 2 owns isolated clone prompts/service; Task 3 prevents dual voice/LLM GPU residency; Task 5 supplies docs and actual GUI evidence.
- Every service uses the same `engine_id` plus `profile_id` selection boundary, fixed port, profile payload, failure behavior, and no-fallback rule.
- No placeholder, default promotion, automatic model download, unbounded service startup, VTS change, or legacy-test restoration remains.
