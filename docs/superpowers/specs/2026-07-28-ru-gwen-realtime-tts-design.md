# Ru Gwen near-realtime TTS design

## Goal

When Ru uses a cloud conversation backend, a short completed reply must begin
audio playback within two seconds in normal warm conditions, while retaining
the existing Gwen Vietnamese voice-clone output and VTube Studio lip-sync.

## Scope

- Keep `g-group-ai-lab/gwen-tts-0.6B` as the default TTS model.
- Use the local GPU only while the cloud backend is selected.
- Preload the Gwen model and its fixed Ru voice-clone prompt in the owned TTS
  service.
- Split a completed reply at safe sentence boundaries, synthesize the first
  sentence immediately, and synthesize later sentences through one ordered
  queue while audio is playing.
- Preserve hidden VTube metadata filtering and current voice enable/disable
  behaviour.

## Non-goals

- No switch to Qwen 1.7B, G-OmniVoice, or another TTS runtime.
- No claim of true token-to-audio streaming: the installed `qwen_tts` library
  explicitly does not provide it for voice cloning.
- No concurrent local Llama and resident Gwen models on the 6 GB GPU.

## Architecture

When the selected backend is cloud and Gwen voice output is enabled, the GUI
releases the local Llama runtime, starts the owned local Gwen service if needed,
and calls a new warmup endpoint. The service loads Gwen once on CUDA, creates a
`VoiceClonePromptItem` from `tyc_talk.wav` plus `reference_text.txt`, and keeps
both resident for cloud speech.

The GUI transforms display-safe reply text into a sequence of sentence-sized
chunks. It sends the first non-empty chunk to the service, plays its WAV as soon
as the response arrives, then produces and plays later chunks in order through
a single background queue. Chunks may not split inside a word, number, URL, or
VTube control suffix. A conservative character cap prevents one unusually long
sentence from delaying the first playback indefinitely.

The service must synthesize from the cached clone prompt rather than re-reading
and encoding the reference audio for every request. Requests remain serialized:
one model instance and one CUDA generation at a time. The GUI treats the full
audio queue as one speaking session so VTube lip-sync stays active across small
gaps between chunks.

Changing back to a local LLM backend, disabling Gwen, closing the GUI, or a
warmup failure releases the Gwen CUDA model and keeps the existing fallback
behaviour. Edge TTS remains independent and does not prewarm Gwen.

## Failure handling

- If Gwen warmup fails, leave chat usable, show an actionable voice status, and
  do not retain a partial GPU model.
- If a later chunk fails, retain already-playing audio, stop the remaining
  queue, clear VTube speaking state after playback ends, and report the error.
- If the user begins a new reply or closes the GUI, discard queued chunks and
  delete temporary audio files safely.
- If the local Llama backend is selected, do not prewarm or retain Gwen; keep
  the current handoff that prevents GPU contention.

## Acceptance checks

- Unit tests cover sentence chunking, empty/metadata-only text, ordered queue
  behaviour, and lifecycle transitions between cloud and local backends.
- A service test proves voice-clone prompt creation happens once per warm
  service lifetime and is reused by later synthesis requests.
- A live cloud-backend timing probe records time from completed reply to first
  audio playback for a short Vietnamese reply. The warm median and warm
  worst-case of the measured sample must both be below two seconds before the
  change is adopted.
- Manual smoke check verifies natural Vietnamese speech, a short English phrase,
  continuous VTube lip-sync across chunks, and release of GPU memory after
  returning to the local backend.
