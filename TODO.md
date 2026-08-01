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
- [x] Make short Vietnamese-English conversations consistently natural before
  calling any adapter quality-gate promoted.
- [x] Harden the 2026-08-01 review findings: per-run loopback authentication,
  owned backend/voice lifecycle, session-scoped context, canonical stream reset,
  private marker boundaries, cloud-memory consent, bounded inputs/context,
  training provenance, deterministic corpus inputs, regression tests, and CI.
  See `docs/codebase-review-fixes.md` → Round 3.
- [x] Remove the legacy CustomTkinter GUI (`mini_gpt.gui`), its patch scripts,
  the Flask web GUI (`scripts/gui_ru_llama3_1.py`), `Start_Ru.bat`, and the
  GUI-only tests; the active GUI is now Tauri (`gui-tauri/`) backed by
  `mini_gpt.server` (FastAPI). `append_stream_text` moved to `_text_utils.py`.
  See `docs/codebase-review-fixes.md` → Round 5.
- [x] Make the Tauri chat core work end-to-end: server API refactor (settings
  apply on startup, `GET /settings`, serialized turns, bounded history/context,
  safe error paths), WebSocket streaming for llama.cpp GGUF (`--lora` arg
  ordering bug fix, `repeat_penalty` to stop repetition loops), chat UI
  (done/error handling, reconnect, typing indicator, busy state), settings UI
  (load `GET /settings`, web-search toggle, checkpoint path), background
  Tauri-owned authenticated backend start in `start_dev.bat`; see
  `docs/codebase-review-fixes.md` → Round 6.
- [x] Wire the Voice tab in the Tauri GUI to the TTS pipeline: backend voice
  proxy (`/voice/status|tts|warmup|release`, auto-spawn OmniVoice server),
  partial settings update (voice_label/voice_speed), VoiceTab UI, auto-read
  chat replies, mono-1D waveform fix for soundfile. 173 tests pass; see
  `docs/codebase-review-fixes.md` → Round 6.
- [x] Wire voice input (mic → STT) and VTS/avatar into the Tauri GUI: backend
  `/voice/transcribe` (WAV bytes → Google Speech Recognizer vi-VN) and
  `/vts/status|connect|action` (VTSManager thread, semantic facial/body actions,
  catalog codes), mic button in ChatTab (MediaRecorder → WAV 16 kHz → transcribe
  → fill input), VTS controls in VoiceTab, auto-trigger of `[[VTS:...]]` markers
  on chat replies, marker stripping before TTS. 178 tests pass; see
  `docs/codebase-review-fixes.md` → Round 7.
- [x] Add persistent session history + New Session to the Tauri GUI: per-session
  JSON files in `~/.minigpt/sessions/`, auto-title from the first user message,
  `GET /sessions`, `POST /sessions/new`, `POST /sessions/{id}/activate`,
  `DELETE /sessions/{id}`, WS chat scoped to a session id (auto-create when
  missing), legacy `history.json` auto-migration on first run, ChatTab session
  bar with New Session + history panel. 185 tests pass; see
  `docs/codebase-review-fixes.md` → Round 8.
- [ ] Run a manual voice (mic → STT, TTS playback) and VTS smoke test with
  VTube Studio open.
- [x] Re-run the automated Python, frontend, Rust, corpus, dependency, and
  native gates after runtime hardening.
- [ ] Run the manual GUI/voice/VTS smoke test with real devices/services before
  the next adapter evaluation.

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
- [ ] Implement Discord Bot feature: Bring AI into Voice Channels. (STT: CTranslate2 Whisper. TTS: OmniVoice ONNX / Python backend).
