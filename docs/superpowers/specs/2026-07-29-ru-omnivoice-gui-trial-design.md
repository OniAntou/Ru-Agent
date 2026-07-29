# Ru OmniVoice GUI trial design

## Goal

Expose the benchmarked OmniVoice runtime as an explicit experimental voice
choice in Ru's desktop GUI so the user can compare it with Gwen. Gwen remains
the default and OmniVoice is not promoted automatically.

## Scope

- Expand the existing Voice selector to four persisted choices:
  `Ru (Gwen)`, `Yến Nhi (Gwen)`, `Ru (OmniVoice — test)`, and
  `Yến Nhi (OmniVoice — test)`.
- Run OmniVoice only from `D:\LLM\omnivoice_env`, behind a separate local
  Flask service on `127.0.0.1:5052`.
- Reuse the authorized, immutable reference pairs already used by Gwen:
  `tyc_talk.wav` plus `reference_text.txt` for Ru, and `yen_nhi.wav` plus its
  exact Yến Nhi transcript for Yến Nhi.
- Keep one cached clone prompt per selected OmniVoice profile while its
  service is warm. It is created once per service residency, not once per
  sentence.
- Preserve the existing text filtering, sentence queue, playback cleanup,
  VTube `VoiceVolume` lip-sync, and no-VTS-metadata boundaries.

## Architecture

Create `omnivoice_tts_server.py` as the OmniVoice analogue of the current
Gwen loopback service. It owns the isolated model, both named reference
profiles, clone-prompt cache, `/warmup`, `/tts`, `/release`, and `/status`.
Its `/tts` response retains Gwen's `audio_path` and `synthesis_ms` contract,
so the existing playback queue can use a provider-neutral request boundary.

The GUI owns a small voice-selection mapping with both an engine ID and a
profile ID. It starts only the selected service and posts the selected profile
to that service. A provider-aware GPU lifecycle releases the local LLM, then
releases the non-selected voice service before warming the selected one. It
never allows local Llama, Gwen, and OmniVoice to be CUDA-resident together.

## Error handling and persistence

Persist the four selection labels through the existing voice preference key.
Unknown/legacy values migrate to `Ru (Gwen)`. If the OmniVoice environment,
service, CUDA model, reference asset, or synthesis request fails, display a
clear `Voice unavailable` status and leave the chat reply usable. Do not
silently fall back to Gwen, change the saved choice, or retry through another
engine.

`/release` clears the model and both clone prompts, invokes CUDA cache cleanup,
and is safe to call when the service is already cold. The GUI may retain the
chosen service between cloud replies, but releases it before local Llama work
or a voice-engine switch.

## Non-goals

- No default switch from Gwen, no new model download from the GUI, no cloud
  TTS, no concurrent voice-engine residency, no new reference recordings, and
  no VTube mapping change.
- No automatic quality adoption. The GUI choice is a reversible user trial.

## Verification

- Unit tests cover the four-choice mapping, legacy migration, provider request
  payloads, service startup path, per-profile prompt caching, release, and
  OmniVoice failure without fallback.
- Regression tests prove only one selected voice service is warmed and local
  Llama still releases voice GPU state before inference.
- A manual GUI test plays Ru and Yến Nhi with both engines, verifies the saved
  selection survives restart, and checks that VTS metadata never enters audio.
- Gwen stays selected by default after an existing or fresh settings file.
