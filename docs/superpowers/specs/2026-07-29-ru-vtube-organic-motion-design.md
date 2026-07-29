# Ru VTube Studio Organic Speaking Motion Design

## Goal

Make Ru's speaking movement feel less mechanical after the visible sway was
restored. The model should keep moving during speech, but the motion should
have small timing and amplitude variations instead of repeating one obvious
cycle.

## Current behavior

`VTSManager.sway_loop()` writes `LLM_Sway` every 50 ms. The current policy uses
one sine wave at speaking amplitude `14.0` and idle amplitude `5.0`. This is
visible, but its fixed period makes the avatar look robotic.

## Decision

Keep the existing `LLM_Sway` parameter and `VoiceVolume` lip-sync mapping. Add
a pure, deterministic organic sway controller with three layers:

- a dominant slow sway for the body/head direction;
- a smaller secondary oscillator with a different phase and frequency;
- a very small higher-frequency variation that prevents exact repetition.

The combined signal is normalized so speaking remains bounded by `14.0` and
idle remains bounded by `5.0`. A slow envelope changes the effective amount
of motion within those bounds, allowing natural pauses without stopping sway
entirely.

The VTube manager will smooth the output toward each target value using the
monotonic frame delta. This gives a short ease-in when speech starts and an
ease-out when it ends, without injecting a discontinuity into the model.

Micro-actions remain rate-limited and catalog-filtered as implemented today.
They are not driven by the sway waveform, and semantic LLM actions remain
independent. No action metadata reaches chat, TTS, memory, or history.

## Interfaces

`src/mini_gpt/vts_motion.py` will expose:

- the existing amplitude constants and `sway_value` compatibility function;
- an `OrganicSwayController` with `reset()` and `update(now, elapsed,
  speaking)` methods;
- deterministic tests for bounds, non-repeating samples, and speaking/idle
  transition smoothing.

`VTSManager.sway_loop()` will own one controller for its persistent loop. The
loop frequency and `LLM_Sway` request path remain unchanged.

## Safety and acceptance

- `VoiceVolume` remains the only mouth/lip-sync input; no `LLM_MouthOpen`.
- Speaking motion is visibly present but less uniform than a single sine wave.
- Idle motion remains gentle and bounded.
- Transitioning speaking on/off does not jump abruptly.
- Existing micro-action, semantic-action, overlay, and VTS metadata behavior
  remains unchanged.

## Verification

Add focused unit tests for the controller and run the full `.venv` suite,
compile checks, the Gwen/OmniVoice partition, and `git diff --check`.
