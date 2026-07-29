# Ru OmniVoice isolated benchmark design

## Goal

Determine whether k2-fsa OmniVoice can produce Ru's authorized cloned voice
faster than the current Gwen service while preserving natural Vietnamese,
English, Vietnamese-English code-switching, and the existing voice identity.
This is an experiment only: Gwen remains the default until a candidate passes
every gate.

## Scope

- Install OmniVoice into a new `D:\LLM\omnivoice_env` virtual environment;
  do not reuse `.venv` or `gwen_env`.
- Store downloaded OmniVoice weights and Hugging Face cache under
  `D:\LLM\models\omnivoice`.
- Use only Ru's existing authorized clone reference
  `D:\LLM\tyc_talk.wav` and `D:\LLM\reference_text.txt`.
- Add a deterministic benchmark manifest with 12 short utterances: six
  Vietnamese, three English, and three natural Vietnamese-English turns.
- Add a benchmark runner that records model-load time, first-WAV latency,
  total synthesis time, generated-audio duration, real-time factor, GPU peak,
  process RAM, and output paths in a JSON report.
- Run Gwen and OmniVoice under the same no-competing-local-model condition;
  each gets one cold trial and five warm trials per utterance set.

## Non-goals

- No GUI selector, no replacement of Gwen, no change to VTube lip-sync, and no
  new persistent voice profile.
- No training, fine-tuning, reference-audio modification, background daemon,
  CPU cache experiment, or concurrent Llama/Gwen/OmniVoice residency.
- No claim that a published RTF transfers to Ru's RTX 4050 6 GB hardware.

## Execution model

The benchmark runner starts one chosen engine at a time. Before OmniVoice
loads, it verifies that the owned Gwen service and local Llama runtime are not
using the GPU. It uses the same fixed clone reference and manifest for every
run, writes generated WAV files into an ignored experiment-output directory,
and records metrics in a committed JSON report schema. Model files and WAV
outputs are not committed.

The Gwen baseline uses its existing local HTTP service and profile `ru`.
OmniVoice receives the reference audio plus its matching text through the
official local Python API. The runner excludes model-download time from timing,
but records model-load time separately. A failed import, missing reference,
CUDA out-of-memory condition, invalid WAV, or synthesis error creates a failed
report record and ends that candidate run; it never falls back to Gwen.

## Acceptance gate

OmniVoice is rejected if any of these occur:

- Ru's clone identity is clearly lost, or audible artifacts occur in normal
  listening.
- Vietnamese pronunciation, English words, or Vietnamese-English switching
  are worse than Gwen in the same utterance.
- It cannot complete reliably on the RTX 4050 6 GB setup without competing
  with Llama or Gwen.
- Median warm first-WAV latency or real-time factor is not at least 25% better
  than the Gwen baseline.

Only a candidate that passes all automatic checks and an explicit manual
listening review can be proposed for GUI integration. A rejection leaves Gwen
unchanged and preserves a concise report with the exact environment, failed
gate, and cleanup command.

## Verification

- Unit tests cover manifest validation, metric calculation, report shape,
  omitted-download timing, and failure mapping using fake engines; no test
  downloads a model or synthesizes audio.
- The environment setup verifies the installed OmniVoice package and configured
  cache path before any real benchmark begins.
- A real run records Gwen and OmniVoice cold plus five warm trials under the
  same process/GPU constraints, then supplies generated audio files for manual
  review.
- Full retained project tests and the benchmark test suite pass after the
  experiment tooling is added.
