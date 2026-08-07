# Cloud Voice First-Audio Optimization Design

Date: 2026-08-07
Status: Approved direction; written review pending before implementation
Scope: hosted-chat replies that auto-read through local OmniVoice in the WinUI 3 GUI

## Problem and evidence

The measured warm OmniVoice path is substantially faster than the perceived
chat-to-speech delay. A local probe on the RTX 4050 measured 18.9 seconds for
cold model warm-up, 1.06-2.00 seconds for warm `/tts`, and 1.18-1.65 seconds
through the backend voice coordinator. Therefore a 5-10 second real reply is
not explained by warm OmniVoice inference alone.

The current GUI waits for a completed sentence before it queues speech. The
current saved chat backend is Kilo `kilo-auto/free`; its model routing and
time-to-first-token are external and are deliberately not changed in this
iteration. The current runtime also starts the OmniVoice process at most; it
does not automatically load the model and clone prompt before the first reply.

## Decision

Implement three bounded changes together:

1. Warm the selected OmniVoice profile asynchronously when a hosted backend
   with auto-read enabled becomes ready.
2. Permit exactly one early, word-safe first phrase when the first streamed
   reply has no sentence punctuation yet.
3. Record numeric-only, loopback-only end-to-end latency milestones for each
   auto-read reply.

The selected cloud provider, model, prompt, cloud-memory setting, OmniVoice
generation configuration, voice profile, speed, and local-GGUF policy remain
unchanged. `share_memory_with_cloud` remains true by default and is not read
or emitted by telemetry.

## Alternatives considered

### A. Keep sentence-only streaming and tune OmniVoice generation

This preserves prosody but cannot reduce the time spent waiting for the cloud
model to finish its first sentence. The current `vi-normalized-16` warm path
already accounts for roughly 1-2 seconds, so reducing its generation steps is
not the highest-return action and risks Vietnamese quality.

### B. Phrase-first playback with automatic cloud warm-up (selected)

This overlaps the existing local synthesis with earlier cloud output while
keeping the rest of the reply sentence-sized. It removes cold model loading
from a normal hosted first turn without changing provider quality or sending
additional provider requests.

### C. Play a fixed acknowledgement immediately

A cached acknowledgement would improve perceived responsiveness but would not
reduce answer latency and can feel repetitive or interrupt the reply. It is
out of scope unless later listening tests show that the phrase-first path is
insufficient.

## Architecture

### 1. Hosted-cloud warm-up policy

After persisted settings have loaded, the FastAPI lifespan schedules one daemon
warm-up only when all conditions hold:

- the active runner backend is OpenCode or Kilo;
- `auto_read_replies` is enabled; and
- the selected voice is OmniVoice.

The warm-up executes outside the readiness path and goes through
`GpuLifecycle.warm_voice_for_cloud`. It serializes against the existing GPU
lock, warms the selected clone prompt, and releases any local runtime only
after successful warming. A process start, model load, or warm-up failure is
logged locally and does not make the API unavailable.

Saving voice or hosted-backend settings schedules the same deduplicated
warm-up. Switching to local GGUF does not schedule a warm-up; the existing
local-generation lifecycle releases OmniVoice before it consumes the GPU.
There is no concurrent Llama and OmniVoice residency on the 6,141 MiB GPU.

### 2. First-phrase buffering

`VoiceReplySentenceBuffer` remains sentence-first. A complete sentence ending
in the existing supported punctuation is emitted immediately, even if short.
If no sentence boundary appears, it may emit one early phrase only under these
rules:

- wait until at least 48 characters are pending;
- select the latest safe word boundary from character 48 through character 60;
  if that range has none, select the first safe word boundary from character
  61 through character 72; a comma, semicolon, colon, dash, or whitespace is
  safe only when it follows a complete word;
- never split a word, number, marker, or token; if no safe boundary exists by
  character 72, keep buffering until a later delta or final flush;
- after that one early phrase, resume the existing sentence-only behavior for
  the rest of the reply.

This bounds the quality trade-off to the opening phrase and lets local TTS
start before the cloud model emits a full sentence. It does not claim to fix
cloud time-to-first-token.

### 3. Private latency telemetry

The GUI owns a reply-scoped monotonic tracker. It emits one numeric aggregate
to an authenticated loopback endpoint after playback completes, fails, or is
cancelled. The payload permits only these fields:

- `submit_to_first_delta_ms`;
- `submit_to_first_phrase_ms`;
- `phrase_to_first_audio_ms`;
- `submit_to_playback_requested_ms`;
- `outcome` (`completed`, `cancelled`, or `failed`).

The telemetry endpoint rejects unknown fields and non-finite numbers; every
present duration must be between 0 and 120,000 milliseconds. It logs only the
numeric aggregate in the existing local backend log;
it stores and transmits no reply text, audio, request ID, user prompt, VTS
metadata, voice pace marker, history, memory, training data, or cloud data.
Failed telemetry delivery must not affect synthesis or playback.

## Files and boundaries

- `src/mini_gpt/_server_shared.py`: cloud-warm eligibility and one deduplicated
  background warm-up scheduler.
- `src/mini_gpt/routers/settings.py`: schedule a background warm-up after a
  successful relevant settings save; never block the settings response.
- `src/mini_gpt/routers/voice.py`: authenticated, strict numeric telemetry
  endpoint only.
- `gui-winui3/Services/VoiceReplySentenceBuffer.cs`: pure first-phrase
  selection logic with no network or playback dependency.
- `gui-winui3/Services/VoiceReplyLatencyTracker.cs`: pure monotonic milestone
  accumulator that cannot accept text.
- `gui-winui3/Services/VoicePlaybackService.cs`: connect the tracker to reply
  stream, TTS first audio, and first playback request.
- `gui-winui3/Services/ApiService.cs`: best-effort authenticated loopback
  telemetry call.

## Tests and verification

- Python tests prove only hosted/auto-read OmniVoice settings schedule a warm,
  duplicate schedules coalesce, and warm-up failure does not fail startup.
- C# tests prove sentence boundaries win, the first long unpunctuated reply
  emits a word-safe phrase once, and later unpunctuated fragments remain
  buffered until a sentence boundary or flush.
- C# tests prove latency snapshots contain only numeric milestones and a fixed
  outcome; no API accepts a text field.
- API tests prove the telemetry endpoint rejects unknown fields and invalid
  numbers while accepting a valid numeric report.
- Full Python tests, GUI tests, GUI publish, and a sequential live local TTS
  probe run after implementation. The live cloud provider benchmark is a
  separate approved-next step and is not part of this change.

## Acceptance criteria

- API readiness is not delayed by background OmniVoice warm-up.
- A hosted first reply with a long unpunctuated opening can start synthesis at
  a safe phrase boundary without splitting a word or producing duplicate
  speech.
- The second and later units retain sentence-first prosody.
- No raw text, audio, identifiers, or VTS/voice markers appear in telemetry,
  local history, memory, or cloud requests.
- Local GGUF continues to release OmniVoice before local GPU generation.
- The measured report can distinguish cloud first-delta delay, phrase-buffer
  delay, first TTS audio delay, and playback-open delay.
