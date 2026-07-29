# Ru OmniVoice Pronunciation Tuning Design

## Goal

Improve OmniVoice Vietnamese pronunciation using only the installed
`k2-fsa/OmniVoice` model and existing Ru/Yến Nhi reference pairs, while keeping
at least a 2x warm first-playable-WAV latency advantage over Gwen.

## Scope and constraints

- Keep `Ru (Gwen)` as the GUI default and retain both OmniVoice labels as trial
  choices only.
- Do not record, download, train, fine-tune, or replace a model/reference.
- Keep all runtime assets, cache, benchmark WAVs, and reports on `D:\LLM`.
- Keep the existing one-local-engine GPU lifecycle and the no-fallback behavior.
- Preserve sentence queue, VTube metadata filtering, and `VoiceVolume` lip-sync.
- The baseline is the current OmniVoice configuration: no explicit language,
  `normalize_text=False`, and `num_step=16`.

## Candidate matrix

The benchmark evaluates the existing Ru and Yến Nhi prompts separately over the
same short Vietnamese, English, and mixed-language calibration texts.

1. Baseline: `num_step=16`, current request behavior.
2. Text-aware baseline: `language="Vietnamese"`, `normalize_text=True`,
   `num_step=16`.
3. Quality candidates: the text-aware configuration at `num_step=20` and
   `num_step=24`.

The benchmark must retain the complete WAV, synthesis time, generated-audio
duration, RTF, process/GPU sample when available, and exact profile/configuration
for every run. It must not alter the GUI during evaluation.

## Selection gate

A candidate may replace the current OmniVoice trial configuration only when:

- every calibration request succeeds and produces a playable WAV;
- its median warm first-playable-WAV time is no more than half Gwen's measured
  median warm time (at least 2x faster);
- it is not worse than the current 16-step OmniVoice baseline on measured
  latency or RTF without a clearly better manual pronunciation outcome; and
- manual listening confirms clearer Vietnamese pronunciation for both Ru and
  Yến Nhi. English and Vietnamese-English switching must remain intelligible.

If no configuration passes, leave the GUI configuration at 16 steps and record
the trial as non-adopted. Passing this tuning gate does not promote OmniVoice to
the default; Gwen remains the default until a separate voice-quality decision.

## Runtime integration

For the manual listening trial, `omnivoice_tts_server.py` owns the selected
immutable settings after the automated gate passes. GUI requests remain
profile-only and do not expose model tuning controls. This prevents a user
setting from silently bypassing the benchmark gate and keeps cached clone
prompts reusable. This trial-only selection does not promote OmniVoice or
change the Gwen default; recorded manual approval remains required.

## Verification

- Focused tests cover candidate configuration validation, benchmark/report
  metadata, and the server's exact `generate(...)` arguments without loading
  CUDA in tests.
- Run the focused quality benchmark through `D:\LLM\omnivoice_env`.
- Manually listen to both profiles, then run the full Python test suite and
  compile all runtime sources.
- Document only the measured result and chosen trial configuration in the
  README and handoff.

## Self-review

- No new voice assets, model downloads, automatic default promotion, or GUI
  tuning controls are in scope.
- The 2x speed requirement is explicit and evaluated against the existing Gwen
  baseline.
- A failing manual or automatic gate leaves the current reversible trial intact.
