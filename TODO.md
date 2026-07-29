# Ru Roadmap

## Active Llama path

- [x] Retain the Llama 3.1 8B v6 QLoRA HF fallback.
- [x] Bound GUI conversation context; persistent memory and search can be
  disabled in Settings.
- [x] Remove discontinued Qwen LLM, Gemma, MiniGPT, and V5 corpus code/data
  from the supported runtime surface. Gwen's isolated Qwen TTS dependency remains active.
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
- [ ] Complete manual bilingual/held-out review and verified physical-VRAM
  gate for v6.2 before calling it quality-gate promoted.
- [x] Finish the bounded v6.1 candidate and its comparison evaluation; its
  training manifest reports a 5,900 MiB physical peak under the 6,144 MiB cap.
- [ ] Make short Vietnamese-English conversations consistently natural before
  calling any adapter quality-gate promoted.

## Deferred capabilities

- [ ] Voice input/output after the text-only Llama path passes its quality gate.
- [ ] Add a local CPU Faster-Whisper provider for Vietnamese microphone input
  and Japanese reference-audio transcription; do not load it onto the 6 GB GPU
  alongside Llama or Gwen.
- [ ] Continue manual listening through the GUI with local
  `g-group-ai-lab/gwen-tts-0.6B` and `tyc_talk.wav` as the Japanese reference.
  Gwen is connected for experimental automatic playback; keep evaluating
  Vietnamese pronunciation and the Tsukuyomi-like voice quality.

- [ ] After voice quality is stable, queue sentence-sized TTS jobs while
  preserving playback order; evaluate VAD/barge-in only after that foundation
  passes its quality gate.
- [ ] Avatar and vision work after the text and voice paths are stable.
