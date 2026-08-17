# Ru Roadmap

## Active Architecture: Cloud-First & External Local Server (Ollama / LM Studio)

- [x] Switched runtime defaults to Cloud (OpenCode Zen / Kilo Gateway).
- [x] Added first-class OpenAI-compatible support for local servers: Ollama (`http://localhost:11434/v1`), LM Studio (`http://localhost:1234/v1`), and Custom OpenAI API endpoints.
- [x] Purged in-process local model weights (`models/gguf/`, `models/lfm2.5-2.6b-hf/`, `models/vision/`) to reclaim disk space while preserving `models/omnivoice/` for TTS voice synthesis.
- [x] Synced settings, dynamic model listing (`GET /models`), and WinUI 3 SettingsView across Python Worker, .NET Backend, and Desktop Client.

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
