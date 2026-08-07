# Cloud Voice First-Audio Optimization Design

Date: 2026-08-07
Status: Implemented; direct Kilo stream correction verified; GUI first-audio acceptance and provider A/B pending
Scope: hosted-chat replies that auto-read through local OmniVoice in the WinUI 3 GUI

## Problem and evidence

The measured warm OmniVoice path is substantially faster than the perceived
chat-to-speech delay. A local probe on the RTX 4050 measured 18.9 seconds for
cold model warm-up, 1.06-2.00 seconds for warm `/tts`, and 1.18-1.65 seconds
through the backend voice coordinator. Therefore a 5-10 second real reply is
not explained by warm OmniVoice inference alone.

Before this iteration, the GUI waited for a completed sentence before it queued
speech, and the runtime started the OmniVoice process at most rather than
loading its model and clone prompt before the first reply. The current saved
chat backend at that time was Kilo `kilo-auto/free`; its model routing and
time-to-first-token were external and deliberately unchanged by that iteration.

## Kilo normal-chat follow-up (2026-08-07)

A later live diagnosis isolated the much larger delay in normal Kilo chat to
provider-side reasoning. With the same full Ru chat request, the active
reasoning-capable Kilo model first streamed after 7,458.9 ms under its default
reasoning behavior and after 1,423.9 ms with `reasoning.effort=none`. The
implemented `ModelRunner` path was then probed with normal chat generation and
first streamed in 1,452.5 ms.

Normal Kilo chat now sends `reasoning.effort=none` by default. A persisted GUI
toggle, **Reasoning cho tác vụ phức tạp**, lets the user re-enable provider
reasoning when the quality trade-off is appropriate. This does not change the
selected provider/model, the screen-observation route, the prompt, or
`share_memory_with_cloud`, which remains true by default. The direct probe did
not exercise GUI rendering, first-phrase buffering, OmniVoice synthesis, or
audible playback, so it is not an end-to-end first-audio acceptance result.

## Decision

Implement three bounded changes together:

1. Warm the selected OmniVoice profile asynchronously when a hosted backend
   with auto-read enabled becomes ready.
2. Permit exactly one early, word-safe first phrase when the first streamed
   reply has no sentence punctuation yet.
3. Record numeric-only, loopback-only end-to-end latency milestones for each
   auto-read reply.
4. Disable provider reasoning for normal Kilo chat by default, with a
   user-visible opt-in for complex tasks.

The selected cloud provider, model, prompt, cloud-memory setting, OmniVoice
generation configuration, voice profile, speed, and local-GGUF policy remain
unchanged. The only Kilo-chat request-shape change is the explicit default
`reasoning.effort=none`; `share_memory_with_cloud` remains true by default.

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
- select the first safe word boundary from character 48 through character 60;
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
  background warm-up scheduler, plus the persisted Kilo reasoning default.
- `src/mini_gpt/headless_runner.py`: applies `reasoning.effort=none` only to
  normal Kilo chat while the setting is disabled.
- `src/mini_gpt/routers/settings.py`: schedule a background warm-up after a
  successful relevant settings save and carry the Kilo reasoning setting into
  the active runner.
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
- `gui-winui3/Models/Models.cs` and `gui-winui3/Views/SettingsView.*`: persist
  and expose the Kilo reasoning opt-in only for the Kilo backend.

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

## Implementation verification (2026-08-07)

- Sequential automated gates passed: Python `pytest -q` (`411 passed`, two
  third-party deprecation warnings), WinUI tests (`27 passed`), and
  `cmd /c build.bat` (self-contained publish). `git diff --check` reported no
  whitespace errors.
- A temporary, HF-offline local OmniVoice loopback service warmed the `ru`
  profile in 16,345.76 ms. Its representative warm `/tts` request returned a
  WAV in 1,768.04 ms (server synthesis header: 1,682 ms). The task then issued
  `/release`, stopped only its temporary server processes, and verified that
  port 5052 and those GPU PIDs were released.
- This is local-runtime evidence only. No cloud chat was invoked, no live cloud
  p50/p95 was measured, no user listened to the generated audio, and no
  provider/model A/B comparison was run.
- The Kilo follow-up passed red-green runtime, settings, startup, and GUI DTO
  contract tests. The full sequential gates then passed: Python `416 passed`,
  WinUI `46 passed`, and self-contained GUI publish. The direct Kilo
  `ModelRunner` probe measured first delta at 1,452.5 ms with reasoning
  disabled; GUI/audio acceptance remains pending.

## Historical Phase 1 context

`2026-08-07-voice-quality-latency-design.md` remains the historical Phase 1
record for local OmniVoice quality and latency work. This design extends that
record with hosted first-audio scheduling and privacy-safe loopback telemetry;
it does not replace or delete Phase 1 acceptance work.

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
