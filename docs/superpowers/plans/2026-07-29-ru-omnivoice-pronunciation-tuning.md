# Ru OmniVoice Pronunciation Tuning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Improve the existing OmniVoice trial's Vietnamese pronunciation by evaluating explicit Vietnamese text handling and 16/20/24 denoising-step configurations for both Ru and Yến Nhi, while retaining a minimum 2x median warm first-playable-WAV speed advantage over Gwen.

**Architecture:** A small immutable generation-settings module is the single source of truth for the current baseline and three candidates. The existing generic benchmark engine receives one settings object and records its exact values on every result. A new D:-only PowerShell coordinator runs Gwen once, then each OmniVoice profile/configuration combination sequentially, writes a reviewable aggregate report, and never touches the GUI. `omnivoice_tts_server.py` consumes only the explicitly approved immutable settings after both automated and manual gates pass.

**Tech Stack:** Python 3.13/3.11, dataclasses, pytest, existing `mini_gpt.tts_benchmark`, OmniVoice local Python API, PyTorch CUDA, Flask, PowerShell, JSON.

## Global constraints

- Use the installed `k2-fsa/OmniVoice` model plus only the existing Ru and Yến Nhi audio/transcript pairs. Do not download a model, record audio, train, fine-tune, alter a reference, or add a GUI control.
- Retain `Ru (Gwen)` as the default. OmniVoice remains a reversible test choice, and a failed or unreviewed candidate must leave its current 16-step runtime behavior intact.
- Keep source, caches, generated WAVs, reports, and temporary benchmark data on `D:\LLM`. Commit no model, environment, generated WAV, or ignored `eval_results` artifact.
- Keep one local GPU workload at a time. The coordinator may release only its owned Gwen process; it must abort if a local Ru Llama workload owns CUDA and never kill an arbitrary process.
- Preserve current profile-only GUI requests, clone-prompt caching, sentence queueing, VTube metadata filtering, and `VoiceVolume` lip-sync. VTS metadata must never enter the benchmark manifest, reports' text payloads, TTS, history, or memory.
- Treat a completed WAV as first-playable WAV, not streaming or first-token latency. Do not reinstate removed legacy tests; add only targeted tests.
- Passing speed is necessary but insufficient: both Ru and Yến Nhi require successful WAVs, a per-profile 2x speed result against Gwen, and explicit manual listening approval before server adoption.

## Configuration contract

- Baseline `baseline-16`: `language=None`, `normalize_text=False`, `num_step=16`; it represents the current production trial exactly.
- Candidate `vi-normalized-16`: `language="Vietnamese"`, `normalize_text=True`, `num_step=16`.
- Candidate `vi-normalized-20`: `language="Vietnamese"`, `normalize_text=True`, `num_step=20`.
- Candidate `vi-normalized-24`: `language="Vietnamese"`, `normalize_text=True`, `num_step=24`.
- Each OmniVoice `generate(...)` call must retain `text` and `voice_clone_prompt`, then pass exactly the selected settings' `language`, `normalize_text`, and `num_step`. `language=None` remains an explicit baseline argument rather than an accidental Python default.
- Each report record must retain `voice_profile`, `generation_config_id`, `generation_settings`, phase, utterance, path, timing, duration, RTF, status, and any resource sample. The aggregate report must retain Gwen's baseline summary and a per-profile automatic gate result for every OmniVoice configuration.

## Task 1: Centralize and test immutable OmniVoice settings

**Files:** `src/mini_gpt/omnivoice_tuning.py`, `tests/test_omnivoice_tuning.py`.

- [ ] Write failing tests for an immutable `OmniVoiceGenerationSettings` dataclass with `config_id`, `language`, `normalize_text`, and `num_step`; reject blank configuration IDs, blank non-null language values, non-boolean normalization values, and non-positive/non-integer step counts. Assert its `generate_kwargs()` returns all three exact values, including `language: None` for `baseline-16`.
- [ ] Write failing tests that assert `BASELINE_SETTINGS`, `TEXT_AWARE_16_SETTINGS`, `TEXT_AWARE_20_SETTINGS`, and `TEXT_AWARE_24_SETTINGS` have precisely the configuration-contract values and that `CANDIDATE_SETTINGS` preserves the benchmark order: baseline, 16, 20, 24.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_omnivoice_tuning.py -q`; it must fail because `mini_gpt.omnivoice_tuning` does not exist.
- [ ] Implement the frozen dataclass and exported constants in `src/mini_gpt/omnivoice_tuning.py`. Keep it dependency-free: it may not import Flask, torch, OmniVoice, GUI code, or a voice asset.
- [ ] Rerun the focused test and expect PASS.
- [ ] Commit only `src/mini_gpt/omnivoice_tuning.py` and `tests/test_omnivoice_tuning.py` with message `feat: define OmniVoice pronunciation settings`.

## Task 2: Make the benchmark configuration- and profile-aware

**Files:** `scripts/benchmark_tts.py`, `tests/test_benchmark_tts_cli.py`.

- [ ] Write failing fake-model tests in `tests/test_benchmark_tts_cli.py` that load an `OmniVoiceBenchmarkEngine` with each settings object, synthesize a temporary WAV, and assert the captured `generate(...)` keyword arguments include the same `text`, cached `voice_clone_prompt`, `language`, `normalize_text`, and `num_step` supplied by the settings object. Do not import OmniVoice or CUDA in the test.
- [ ] Write failing report tests that call `run_benchmark(..., voice_profile="yen_nhi")` using a fake engine and assert every success and failure record carries `voice_profile`, while the top-level report carries the engine's exact `generation_config_id` and `generation_settings`. Assert Gwen receives no fabricated OmniVoice settings.
- [ ] Write failing CLI tests for `--omnivoice-config` accepting only `baseline-16`, `vi-normalized-16`, `vi-normalized-20`, and `vi-normalized-24`; default it to `baseline-16`; and `--voice-profile` accepting only `ru` or `yen_nhi`. Assert invalid selections exit before a model is constructed.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_benchmark_tts_cli.py -q`; it must fail before the adapter/report changes exist.
- [ ] Modify `OmniVoiceBenchmarkEngine` to receive an `OmniVoiceGenerationSettings` object rather than an unconstrained integer. In `synthesize`, call `model.generate(text=text, voice_clone_prompt=self._prompt, **settings.generate_kwargs())`. Keep model import, cache configuration, CUDA validation, prompt construction, WAV writing, and release behavior unchanged.
- [ ] Extend `run_benchmark` with an explicit `voice_profile` argument and one optional engine metadata hook. Merge the stable metadata into the top-level report and every record, including load failures, without mutating a shared metadata dictionary. Implement CLI parsing that resolves the selected immutable setting before invoking `build_engine`; Gwen continues to work with its existing loopback contract and reports only its voice profile.
- [ ] Rerun `& .\.venv\Scripts\python.exe -m pytest tests\test_benchmark_tts_cli.py tests\test_tts_benchmark.py -q`; expect PASS with fakes only.
- [ ] Commit only `scripts/benchmark_tts.py` and `tests/test_benchmark_tts_cli.py` with message `feat: record OmniVoice tuning configuration`.

## Task 3: Add a safe full-matrix pronunciation coordinator

**Files:** `scripts/run_omnivoice_pronunciation_tuning.ps1`, `tests/test_omnivoice_pronunciation_tuning_runner.py`.

- [ ] Write failing static/behavioral tests that read the coordinator text and assert: it resolves only paths below `D:\LLM`; uses the existing 12-line `data\evaluations\ru_tts_benchmark_v1.jsonl`; runs Gwen once for `ru`; runs all four configuration IDs for both `ru` and `yen_nhi`; passes each profile's matching existing audio and transcript; uses `D:\LLM\omnivoice_env\Scripts\python.exe` for OmniVoice; supports `-WhatIf`; requires explicit `-ConfirmRun`; and writes `tuning-summary.json` below a timestamped `eval_results\omnivoice-pronunciation` root.
- [ ] Write failing tests that reject `Stop-Process`, package installation, model download, GUI launch, GUI-setting writes, and an unscoped process launcher. Permit `Start-Process` only for the known `D:\LLM\gwen_env\Scripts\python.exe` plus `gwen_tts_server.py` owned-service command; require the existing local-Ru-GPU blocker pattern and a Gwen `/release` `finally` block.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_omnivoice_pronunciation_tuning_runner.py -q`; it must fail because the coordinator does not exist.
- [ ] Implement `scripts/run_omnivoice_pronunciation_tuning.ps1` with parameters `OutputRoot`, `ConfirmRun`, `OverwriteOutput`, and `WhatIf`. Resolve project-root-relative paths, refuse output reuse unless `OverwriteOutput` is supplied, validate both reference WAV/text pairs and both Python runtimes, and preflight the existing Ru GPU blocker before any benchmark action.
- [ ] In `WhatIf`, print the resolved Gwen command and all eight exact OmniVoice invocations, then exit without making a directory, starting a service, loading CUDA, changing a setting, or creating a WAV. In a real confirmed run, start only the known owned Gwen loopback service when `/status` is unavailable, invoke the Gwen benchmark once with `--voice-profile ru`, always release Gwen in `finally`, then invoke the generic benchmark sequentially for `ru` and `yen_nhi` across `baseline-16`, `vi-normalized-16`, `vi-normalized-20`, and `vi-normalized-24`.
- [ ] Write `tuning-summary.json` with schema version, `status: "pending_manual_review"`, baseline report path/summary, the exact output/report path and summary for each of the eight profile/configuration runs, and a per-profile automatic speed verdict. A verdict is `pass` only when that report has zero failures and its `median_warm_first_playable_wav_seconds` is a finite value no greater than Gwen's finite median divided by two; otherwise record `fail` with the concrete condition. Never choose or alter a server configuration in this script.
- [ ] Rerun the focused runner test and then `powershell -ExecutionPolicy Bypass -File .\scripts\run_omnivoice_pronunciation_tuning.ps1 -WhatIf`; expect PASS and printed commands only.
- [ ] Commit only `scripts/run_omnivoice_pronunciation_tuning.ps1` and `tests/test_omnivoice_pronunciation_tuning_runner.py` with message `feat: add OmniVoice pronunciation matrix runner`.

## Task 4: Produce real evidence and hold the manual quality gate

**Files:** ignored `eval_results\omnivoice-pronunciation\<timestamp>\`; no tracked source change before the gate is decided.

- [ ] Verify `git check-ignore -v eval_results\omnivoice-pronunciation\probe.txt` reports an ignore rule, then remove only the disposable probe file if it was created. Verify no local Ru Llama process has CUDA compute, and verify Gwen service can answer `/status` or can be started through the existing owned coordinator path.
- [ ] Run `powershell -ExecutionPolicy Bypass -File .\scripts\run_omnivoice_pronunciation_tuning.ps1 -ConfirmRun`. Do not start the GUI, download packages, or change a selected voice while it runs.
- [ ] Inspect `tuning-summary.json`: confirm every report has `failed_records: 0`, each candidate/profile record has its exact configuration and profile, and identify only configurations whose two profile speed verdicts are `pass`. Reject a configuration automatically if either profile is missing, errors, lacks a finite median, or exceeds half Gwen's median warm first-playable-WAV time.
- [ ] Listen to representative cold-independent warm WAVs for both profiles and all three language groups: at minimum `warm-01-vi-01.wav`, `warm-01-en-01.wav`, and `warm-01-mix-01.wav` from every speed-passing configuration. Compare each against the corresponding `baseline-16` WAV. Approve only one configuration when both Ru and Yến Nhi have clearer Vietnamese pronunciation and English/code switching remains intelligible; otherwise record `manual_decision: "rejected"` and leave the server baseline unchanged.
- [ ] Add `manual_decision` (`accepted` or `rejected`), `reviewed_config_id`, and a concise profile-by-profile listening note to the already ignored `tuning-summary.json`. Do not infer acceptance from timing alone and do not commit this report.
- [ ] If no candidate is accepted, stop this plan here after running the full regression checks in Task 6; the outcome is a verified non-adoption and no runtime source file changes are permitted.

## Task 5: Adopt only the manually accepted configuration

**Files:** `omnivoice_tts_server.py`, `tests/test_omnivoice_tts_server.py`, `tests/test_omnivoice_tuning.py`.

- [ ] Begin this task only if Task 4 records exactly one `manual_decision: "accepted"` configuration with automatic `pass` for both `ru` and `yen_nhi`. If the decision is rejected or absent, skip this task and preserve `baseline-16`.
- [ ] Write a failing server test that monkeypatches `warm_model`, a fake model, `torch.inference_mode`, and `soundfile.write`, then calls `synthesize_fast` for `ru` and `yen_nhi`. Assert each `generate(...)` receives the accepted immutable settings exactly, retains the matching cached prompt, and never exposes a tuning field through `/tts`.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_omnivoice_tts_server.py tests\test_omnivoice_tuning.py -q`; it must fail because the service still hardcodes `num_step=16`.
- [ ] Replace the hardcoded `num_step=16` in `synthesize_fast` with one module-level import of the accepted constant from `mini_gpt.omnivoice_tuning`, passing `**ACCEPTED_TRIAL_SETTINGS.generate_kwargs()` with the current text and profile prompt. Do not change Flask routes, voice-profile validation, cache lifecycle, ports, model cache, GUI selection names, or default Gwen behavior.
- [ ] Rerun the focused test pair and expect PASS without CUDA, a real model, or an audio device.
- [ ] Commit only `omnivoice_tts_server.py`, `tests/test_omnivoice_tts_server.py`, and any strictly necessary constant-selection change in `src/mini_gpt/omnivoice_tuning.py` with message `feat: apply approved OmniVoice pronunciation tuning`.

## Task 6: Document the measured decision and verify the repository

**Files:** `README.md`, `docs/ANTIGRAVITY_HANDOFF.md`, `docs/superpowers/specs/2026-07-29-ru-omnivoice-pronunciation-tuning-design.md`.

- [ ] Before editing, search the three documents for `OmniVoice`, `pending_manual_review`, `2x`, `baseline-16`, and the older 25% adoption threshold. Keep the historical 2026-07-29 isolated benchmark numbers intact, because they describe a different speed gate.
- [ ] Update `README.md` and `docs/ANTIGRAVITY_HANDOFF.md` with the exact command to run the D:-only pronunciation matrix, the four fixed setting IDs, the required two-profile 2x gate, and the actual final status from Task 4. If rejected, say the GUI still uses the baseline 16-step trial. If accepted, state the precise selected ID and retain Gwen as default.
- [ ] Update the design document's Verification section with the actual report directory, selected/rejected status, and measured median warm results for Gwen plus both profiles of the reviewed configuration. Do not invent or copy measurements before Task 4 supplies them.
- [ ] Remove only documentation that becomes factually obsolete due to the completed decision; do not delete historical benchmark descriptions or unrelated Llama/voice guidance.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest -q` and `& .\.venv\Scripts\python.exe -m compileall -q src scripts omnivoice_tts_server.py`. If Task 5 ran, also start no GUI but use the existing Flask test client suite as the runtime contract. Confirm `git status --short` contains only intended documentation/source changes plus the three pre-existing unrelated files.
- [ ] Commit only the documentation files for this task with message `docs: record OmniVoice pronunciation tuning result`. Do not stage `docs/superpowers/plans/2026-07-29-ru-api-key-security.md`, `src/mini_gpt/_safety_patterns.py`, or `src/mini_gpt/search.py`.

## Plan self-review

- The settings module eliminates accidental drift between benchmark and service arguments, including the current implicit-language baseline.
- The matrix measures the same frozen bilingual manifest across both clone prompts, records exact metadata per record, and evaluates the required 2x speed condition independently for both profiles.
- The coordinator is explicitly D:-only, sequential, confirmation-gated, and incapable of installing, downloading, launching the GUI, or terminating arbitrary user processes.
- Manual listening is a genuine decision point. The service cannot be changed until one configuration succeeds automatically and is explicitly approved for Ru and Yến Nhi.
- The plan retains Gwen as default, adds targeted tests only, checks the full regression suite, and updates/removes documentation only after the measured result is known.
