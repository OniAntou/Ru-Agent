# Gwen experimental GUI autoplay

## Goal

Let the existing Ru GUI automatically speak each completed reply with the
locally tested Gwen voice-cloning model. This is an experiment, not a permanent
voice-provider adoption; the chat response must remain usable if speech fails.

## Chosen approach

Run Gwen behind a separate local Flask service on `127.0.0.1:5051`. The
service uses `D:\LLM\gwen_env`, the locally downloaded
`models\gwen-tts-0.6b` weights, `tyc_talk.wav`, and its matching Japanese
transcript in `reference_text.txt`.

The GUI changes its TTS endpoint to Gwen during this experiment. It keeps the
existing automatic voice toggle and its playback/cleanup flow; no new UI
control is needed. A `run_gwen_tts_server.bat` command starts the service.

## F5 removal

F5 is not a viable fallback for the intended Japanese-reference Vietnamese
speech and is removed completely. After the Gwen path is implemented and
verified, remove the F5 server, launcher, `f5_env`, the local Hugging Face
`SWivid/F5-TTS` cache, and the F5-only test WAVs. Preserve `tyc_talk.wav`,
`tyc_calm.wav`, `reference_text.txt`, the Gwen environment/weights, and Llama
assets.

## VRAM lifecycle

The Llama runtime caches a 4-bit model on the 6 GiB GPU. Gwen cannot coexist
with it reliably. Before starting automatic speech, the GUI calls the existing
Llama cache-clear function. The Gwen service loads the model only for a request
and deletes it, then empties its CUDA cache before returning. The next chat
turn reloads Llama normally.

This trades a short voice-start delay for predictable VRAM use and avoids CPU
offload or simultaneous model residency.

## Request and error contract

`POST /tts` accepts `{"text": "..."}` and returns
`{"audio_path": "..."}` on success, matching the GUI's current contract.
The service rejects an empty target, a missing reference WAV/transcript, or a
generation failure with a JSON error and a non-200 status. The GUI continues to
display the text reply and sets its status to `Voice unavailable: ...` when the
request fails.

The server writes a unique temporary WAV. The existing GUI playback thread
removes it after playback finishes.

## Verification

1. Unit-test the provider request validation and the GUI TTS endpoint/error
   path without loading Gwen.
2. Start the local Gwen service and make a real request using the standard
   short Ru greeting; require a playable 24 kHz mono WAV.
3. Confirm the server releases CUDA memory after the request and that a later
   Llama chat request can load normally.
4. Have the user assess actual Vietnamese intelligibility and cloned voice
   quality before promoting Gwen beyond experimental use.

## Non-goals

- Do not add CPU offload, persistent dual-model GPU residency, remote APIs, or
  automatic learning.
- Do not claim that Gwen's Vietnamese pronunciation has passed the quality
  gate; this integration exists only for user testing.
