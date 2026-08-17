# Ru Roadmap

## Active LFM2.5 path

- [x] Download the native `LiquidAI/LFM2.5-2.6B` checkpoint to
  `models\lfm2.5-2.6b-hf` for future fine-tuning.
- [x] Download `LiquidAI/LFM2.5-2.6B-GGUF` Q4_K_M to
  `models\gguf\LFM2.5-2.6B-Q4_K_M.gguf` and make it the direct llama.cpp GUI
  default.
- [x] Remove the active Llama 3.1 HF, EXL2, base GGUF, and LoRA GGUF assets
  (ADR 0005).
- [ ] Adapt the LFM2 training seam (`tools\training\lfm25\`,
  `configs\ru_lfm2_5_v1.json`) to the LFM2.5 hybrid architecture before
  starting a new fine-tune.
- [ ] Run the LFM2.5 bilingual quality gate and physical-VRAM smoke benchmark.
- [ ] Collect reviewed useful conversation examples.

## Pending product work

- [ ] Collect user-listened hosted audio evidence and numeric live-cloud p50/p95
  before calling the first-audio rollout accepted. Keep the provider/model A/B
  comparison pending until that evidence exists; do not change providers or
  spend credits as part of this task.
- [ ] Run a manual voice (mic → STT, TTS playback) and VTS smoke test with
  VTube Studio open.
- [ ] Run the manual GUI/voice/VTS smoke test with real devices/services before
  the next adapter evaluation.
- [ ] Find and evaluate a replacement local vision model before restoring the
  observer. The replacement must pass latency, resource, structured-output,
  cancellation, and actual OmniVoice playback gates.
- [ ] Keep the structured `/observation/*` cloud-reaction boundary available so
  a future model can be connected without changing the reaction coordinator.

## Deferred capabilities

- [ ] Add a local CPU Faster-Whisper provider for Vietnamese microphone input
  and Japanese reference-audio transcription; do not load it onto the 6 GB GPU
  alongside the active vision or OmniVoice runtime.
- [ ] Design WASAPI system-audio understanding only after local vision passes
  its quality, latency, resource, and process-lifecycle gates.
- [ ] Implement Discord Bot feature: Bring AI into Voice Channels. (STT:
  CTranslate2 Whisper. TTS: OmniVoice.)
