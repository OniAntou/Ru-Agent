# Ru VTube Studio Balanced Motion Design

## Goal

Make Ru's avatar feel alive while she is speaking without the current strong
side-to-side sway dominating the model. Preserve a small amount of sway, add
occasional lightweight motions, and keep expressive actions selected by Ru's
existing catalog-aware LLM path.

## Current behavior

`VTSManager.sway_loop()` injects `LLM_Sway` every 50 ms. While
`is_speaking` is true it uses amplitude `30` and angular speed `4.0`; while
idle it uses amplitude `5` and angular speed `1.5`. Semantic hotkeys and
expressions are dispatched separately after a completed reply.

## Decision

Keep one persistent VTube Studio loop, but split its responsibilities into a
low-amplitude sway layer and a rate-limited micro-action layer.

### Speaking sway

- Speaking sway uses amplitude `8.0` and angular speed `1.35`.
- Idle sway remains amplitude `5.0` and angular speed `1.5`.
- The loop continues writing only `LLM_Sway`; mouth movement remains owned by
  VTube Studio's `VoiceVolume` lip-sync mapping.

### Automatic micro-actions

While speaking, the manager may trigger one action after a randomized interval
of 12-25 seconds. A monotonic clock, a lock, and a last-action timestamp ensure
that actions cannot fire in a tight loop. A new speech session resets the
interval, and stopping speech cancels pending automatic actions.

The neutral pool is derived from the current model catalog and only accepts
safe label/file patterns for small motion: blink/close-eye/eye-restore,
head-tilt/nod, ear, tail, or similarly explicit lightweight motions. It
excludes clothing/body-reveal actions, blush, the `^` pout action, and any
unknown label. If the current model has no matching safe action, no automatic
hotkey is sent.

Semantic facial/body actions selected by the LLM remain independent and are
not replaced by this scheduler. The scheduler must not trigger the same hotkey
while a semantic action is being dispatched.

### Model changes and safety

The manager reuses the current model/hotkey catalog and revalidates the model
before triggering a catalog action. A model switch invalidates the pending
automatic action. Missing or ambiguous labels are skipped without raising to
the GUI. Existing cooldowns and expression deactivation remain unchanged.

## Data flow

1. `is_speaking` becomes true when Gwen playback starts.
2. The persistent loop injects low-amplitude `LLM_Sway` values.
3. Once the randomized interval elapses, the loop selects one safe current
   catalog entry and dispatches it through the existing hotkey path.
4. The loop records the action time and schedules the next interval.
5. When speaking stops, no new automatic action is selected and the next
   speaking session starts a fresh interval.

## Testing

- Unit-test the speaking/idle sway amplitudes and frequencies by injecting a
  deterministic clock.
- Unit-test interval gating, speech-session reset, model-switch invalidation,
  and the safe-label filter.
- Unit-test that semantic action dispatch remains available and that unsafe or
  ambiguous catalog entries are never selected automatically.
- Run the focused VTube tests, then the normal `.venv` suite and the Gwen
  environment test partition.

## Non-goals

- Do not remove sway entirely.
- Do not change `VoiceVolume` lip-sync or add `LLM_MouthOpen`.
- Do not expose action metadata in chat or TTS.
- Do not make the LLM wait for or directly control every micro-motion.
