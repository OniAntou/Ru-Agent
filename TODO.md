# Ru Roadmap

## Active Llama path

- [x] Retain the Llama 3.1 8B v6 QLoRA GUI fallback.
- [x] Bound GUI conversation context and make persistent memory/search opt-in.
- [x] Remove Qwen, Gemma, MiniGPT, and other non-Llama experiment code and data.
- [ ] Collect reviewed useful conversation examples.
- [ ] Prepare and evaluate the next Llama-only adapter with the bilingual
  conversation and physical-VRAM gates.
- [ ] Start the bounded v6.1 candidate only on the user's next explicit
  request (planned for 2026-07-27): 640 steps, 32 deterministic validation
  rows every 20 updates. The 32-step smoke took 543 seconds and passed the
  6,144 MiB physical cap; keep the full held-out bilingual/VRAM gate for
  promotion and do not revert to full validation every 10 updates.
- [ ] Make short Vietnamese-English conversations consistently natural before
  promoting any adapter.

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
