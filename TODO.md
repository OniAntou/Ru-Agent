# Ru Roadmap

## Active Llama path

- [x] Retain the Llama 3.1 8B v6 QLoRA HF fallback.
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
  boundaries, cloud-memory consent, bounded inputs/context, training provenance,
  deterministic corpus inputs, regression tests, and CI.
- [ ] Run a manual voice (mic → STT, TTS playback) and VTS smoke test with
  VTube Studio open.
- [x] Re-run the automated Python, frontend, corpus, dependency, and
  native gates after runtime hardening.
- [ ] Run the manual GUI/voice/VTS smoke test with real devices/services before
  the next adapter evaluation.
- [ ] Finish the live acceptance gates for the implemented primary-monitor
  observer: representative game/YouTube quality, actual Ru OmniVoice playback,
  p50/p95/worst event-to-first-audio latency, three-to-six useful comments per
  active minute, VTS actions, 20 observation cycles, 10 full app exits, forced
  parent termination, and live RAM/VRAM/handle stability. The local Qwen gate,
  DXGI/RAM-only pipeline, hosted reaction API, sidebar control, and process
  ownership are implemented. First audible acceptance on 2026-08-02: user
  confirmed Ru's OmniVoice, `đã phát` reached 6, and post-cloud age stayed
  3.2–4.1 s with 480×270/96-token frames and the ack-semantics fix (skips fell
  from 70% to 12–22%). 20 observation cycles, 10 forced app kills, cloud-loss
  pause, and single-instance checks all passed with no resource growth or
  leftover processes. Remaining manual listening items: a representative
  game/YouTube/subtitle/static/scene-transition quality trial and a dedicated
  ten-minute cadence/repetition review with a known screen; cloud (Kilo) empty
  streams remain an external provider instability to revisit only with user
  approval to change providers or spend credits.

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
