# OmniVoice Measurable Pitch Controls Implementation Plan

> **Status:** Rejected after the 2026-08-07 user listening check. The
> post-generation pitch transform made both presets metallic, and the related
> automatic pace transform has the same artifact class. This plan is retained
> as a historical implementation record; do not execute or ship its runtime
> design unchanged.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace ineffective clone prompt instructions with measurable +/-3
semitone post-generation pitch controls and remove the unsupported whisper
preset.

**Architecture:** `voice_style.py` becomes the single validated mapping from a
stable setting ID to a numeric pitch offset. `omnivoice_tts_server.py` keeps
clone generation prompt-free and shifts only completed non-default audio using
librosa. Existing FastAPI and WebSocket transports continue to carry only a
style ID. WinUI removes the unsupported option.

**Tech Stack:** Python 3.13, numpy, librosa 0.11, Flask loopback OmniVoice,
FastAPI/Pydantic, WinUI 3/.NET 10, pytest, xUnit.

## Global Constraints

- Work directly on user-approved `main`; preserve the user-owned `build.bat`.
- Keep `share_memory_with_cloud` true by default.
- Do not add a model, a voice prompt, or concurrent heavyweight GPU work.
- Keep voice-style data out of visible/persisted conversation content and VTube
  metadata.
- Run Python, .NET, and live GPU checks sequentially.

### Task 1: Establish the behavior with failing tests

**Files:**
- Modify: `tests/test_voice_style.py`
- Modify: `tests/test_omnivoice_tts_server.py`
- Modify: `tests/test_server_api.py`
- Modify: `tests/test_voice_stream.py`
- Modify: `gui-winui3.Tests/SettingsDtoTests.cs`

- [x] Add a table-driven style contract test asserting literal offsets 0.0,
  -3.0, and 3.0 and that saved `whisper` resolves to `original`.
- [x] Add a real sine-wave test that launches `omnivoice_env\\Scripts\\python.exe`,
  calls the future `apply_voice_pitch` on a 24 kHz 220 Hz fixture, and asserts
  duration preservation plus the two hand-derived target frequencies. This
  avoids installing librosa into the unrelated project test environment.
- [x] Add transport tests that reject fresh `whisper`, and change stream/request
  fixtures to `low_pitch` or `high_pitch`.
- [x] Add a server test proving non-default styles call pitch processing with
  the literal offset and never attach `instruct` to `model.generate`.
- [x] Run the focused Python and xUnit tests and verify they fail because the
  numeric contract and processor do not yet exist.

### Task 2: Implement the smallest measured runtime change

**Files:**
- Modify: `src/mini_gpt/voice_style.py`
- Modify: `src/mini_gpt/omnivoice_tts_server.py`
- Modify: `gui-winui3/Views/VoiceView.xaml`

- [x] Replace `VoiceStyle.instruct` with `VoiceStyle.pitch_semitones` and retain
  only the three supported IDs.
- [x] Add `apply_voice_pitch(audio, semitones)` beside existing speed processing:
  return a one-dimensional float waveform unchanged at 0.0; otherwise invoke
  `librosa.effects.pitch_shift(..., sr=24000, n_steps=semitones,
  res_type="soxr_hq")`.
- [x] Generate all clone styles without `instruct`; apply pace/speed first and
  the selected pitch offset second.
- [x] Remove the whisper ComboBox item while retaining original/low/high tags.
- [x] Run the same focused tests and verify they pass.

### Task 3: Verify user-visible behavior and document actual evidence

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-07-omnivoice-measurable-pitch-design.md`
- Modify: `docs/superpowers/specs/2026-08-07-omnivoice-voice-style-presets-design.md`
- Modify: `docs/superpowers/plans/2026-08-07-omnivoice-measurable-pitch.md`

- [x] Run focused Python tests, then the full Python suite and WinUI tests
  sequentially; run `git diff --check`.
- [x] Build the Release GUI using `cmd /c build.bat` without modifying the
  script.
- [x] Restart only the verified OmniVoice service, generate sequential Ru
  original/low/high probes, measure F0 and processing time, and preserve no
  user text in committed artifacts.
- [x] Update documentation with only actual timing, frequency, and listening
  evidence. Mark the old prompt-based design as superseded, not deleted.
