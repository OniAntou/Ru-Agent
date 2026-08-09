# Ru Roadmap

## Active LFM2.5 path

- [x] Download the native `LiquidAI/LFM2.5-2.6B` checkpoint to
  `models\lfm2.5-2.6b-hf` for future fine-tuning.
- [x] Download `LiquidAI/LFM2.5-2.6B-GGUF` Q4_K_M to
  `models\gguf\LFM2.5-2.6B-Q4_K_M.gguf` and make it the direct llama.cpp GUI
  default.
- [x] Remove the active Llama 3.1 HF, EXL2, base GGUF, and LoRA GGUF assets.
- [x] Remove the legacy v5, v6, and v6.1 conversation datasets; retain only
  v6.2, v6.2_fresh, and v6.3 dataset directories.
- [ ] Adapt the historical Llama QLoRA trainer and target modules for the LFM2
  hybrid architecture before starting a new fine-tune.
- [ ] Run the LFM2.5 bilingual quality gate and physical-VRAM smoke benchmark.

## Historical Llama 3.1 path

- [x] Preserve the historical Llama 3.1 QLoRA checkpoints and evaluations;
  obsolete HF fallback/base model assets were removed during the LFM2.5 switch.
- [x] Bound GUI conversation context; persistent memory and search can be
  disabled in Settings.
- [x] Remove discontinued Qwen LLM, Gemma, MiniGPT, and V5 corpus code/data
  from the supported runtime surface.
- [ ] Collect reviewed useful conversation examples.
- [x] Prepare a fresh v6.2 roleplay candidate with train-only, safety-filtered
  imports from approved conversations and local memory (1,200 authored + 39
  accepted calibration rows; 44 rejected).
- [x] Train the bounded v6.2 candidate: 480 steps, 2,630 seconds, validation
  loss 0.2060; adapter is recorded but physical-VRAM sampling was unavailable.
- [x] Run the deterministic 12-case comparison: v6 `11/12`, v6.1 `12/12`,
  v6.2 `12/12`; v6 failed one Vietnamese context-work case.
- [x] Run the harder 36-case comparison: v6 `158/360` (13 hard fails), v6.1
  `180/360` (11 hard fails), v6.2 `205/360` (7 hard fails); all strict gates
  failed, so no adapter was promoted.
- [x] Make the v6.2 LoRA GGUF the operational GUI default with v6.1/v6
  fallback; the strict quality gate remains visibly failed.
- [x] Prepare the new v6.3 targeted dataset: 180 authored rows, 108/36/36
  train/validation/held-out, 360-step and 9,600-second training cap.
- [x] Refresh the active Ru contract and v6.3 rows: no forced `Nya~`, bounded
  contextual profanity metadata, and an authoritative OpenCode cloud prompt.
- [x] Route Ru emotion through VTS: no Unicode emoji or ASCII emoticons in chat,
  with natural prose fallback when VTS is unavailable.
- [x] Migrate Desktop App GUI to Native WinUI 3 C# (.NET 10 / Windows App SDK 1.6+) with Fluent Design, Mica backdrop, Job Object backend process management, strongly-typed REST and WebSocket streaming clients.
- [ ] Complete manual bilingual/held-out review and verified physical-VRAM
  gate for v6.2 before calling it quality-gate promoted.
- [x] Finish the bounded v6.1 candidate and its comparison evaluation; its
  training manifest reports a 5,900 MiB physical peak under the 6,144 MiB cap.
- [x] Make short Vietnamese-English conversations consistently natural before
  calling any adapter quality-gate promoted.
- [x] Harden the codebase: per-run loopback authentication, owned backend/voice
  lifecycle, session-scoped context, canonical stream reset, private marker
  boundaries, configurable cloud-memory sharing (enabled by default), bounded
  inputs/context, training provenance,
  deterministic corpus inputs, regression tests, and CI.
- [x] Add the measured OmniVoice baseline manifest, named generation
  configurations, authenticated sentence streaming, ordered WinUI playback,
  cancellation, and privacy-safe playback acknowledgements. Live listening and
  latency promotion remain separate acceptance gates below.
- [x] Implement hosted-only OmniVoice background warm-up, one word-safe opening
  phrase before sentence punctuation, and numeric-only local loopback latency
  telemetry. Local GGUF keeps its existing GPU lifecycle; these changes do not
  measure or claim a cloud latency improvement.
- [x] Disable normal Kilo-chat reasoning by default, with a persisted Settings
  toggle to re-enable it for complex tasks. A direct post-change ModelRunner
  probe reached its first Kilo delta in 1,452.5 ms; this is provider-stream
  evidence only, not GUI first-audio acceptance. Cloud-memory sharing remains
  enabled by default.
- [x] Run the Task 4 integrated automated gate sequentially: Python `411`
  passed, WinUI tests `27` passed, self-contained publish passed, and
  `git diff --check` had no whitespace errors.
- [ ] Collect user-listened hosted audio evidence and numeric live-cloud p50/p95
  before calling the first-audio rollout accepted. Keep the provider/model A/B
  comparison pending until that evidence exists; do not change providers or
  spend credits as part of this task.
- [ ] Run a manual voice (mic → STT, TTS playback) and VTS smoke test with
  VTube Studio open.
- [x] Re-run the automated Python, frontend, corpus, dependency, and
  native gates after runtime hardening.
- [ ] Run the manual GUI/voice/VTS smoke test with real devices/services before
  the next adapter evaluation.
- [x] Temporarily disable the local screen-observation path because the pinned
  Qwen3-VL model was too slow. The WinUI capture/metrics client and local
  provider tooling were removed; the downloaded vision weights remain on disk
  but are not loaded by the active runtime.
- [ ] Find and evaluate a replacement local vision model before restoring the
  observer. The replacement must pass latency, resource, structured-output,
  cancellation, and actual OmniVoice playback gates.
- [ ] Keep the structured `/observation/*` cloud-reaction boundary available so
  a future model can be connected without changing the reaction coordinator.

## Deferred capabilities

- [ ] Revisit the v6 corpus/lineage safety decision (owner deferred on
  2026-08-04): v6 rows contain explicit/unsafe content and v6 remains the
  `source_adapter_dir` of v6.1/v6.2/v6.3; a clean-data retrain needs explicit
  approval and a new adapter lineage.
- [ ] Add a local CPU Faster-Whisper provider for Vietnamese microphone input
  and Japanese reference-audio transcription; do not load it onto the 6 GB GPU
  alongside the active vision, Llama, or OmniVoice runtime.
- [ ] Design WASAPI system-audio understanding only after local vision passes
  its quality, latency, resource, and process-lifecycle gates.
- [ ] Implement Discord Bot feature: Bring AI into Voice Channels. (STT: CTranslate2 Whisper. TTS: OmniVoice ONNX / Python backend).
