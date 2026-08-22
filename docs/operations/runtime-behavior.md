# Current runtime behavior

Status: operating contract for the Architecture v2 branch, 2026-08-21.

This document keeps current runtime decisions separate from implementation
history. Where automated and live evidence differ, the narrower claim wins.

## Conversation and generation

The cloud-first desktop default is OpenCode Zen. Hosted OpenCode Zen and Kilo
Gateway providers are the supported cloud adapters; OpenAI-compatible custom
adapters remain available for explicitly configured external services. Ru no
longer ships or starts an in-process conversational model. Switching providers
must preserve bounded context, cancellation, streamed visible-text
normalization, and public error mapping by ASP.NET Core. Stale `llama.cpp` or
GGUF settings migrate to the cloud default and cannot select a removed runtime.

Ru mirrors Vietnamese, English, and mixed-language turns. In Vietnamese Ru
calls the user `anh` and itself `em`. Chat contains no Unicode emoji or ASCII
emoticons. Search commands, model controls, voice pace, VTS actions, segment
identifiers, and transport timings are private metadata and must be removed
before display, TTS, history, memory, logs, or reviewed examples.

## Voice output

OmniVoice is the supported local TTS engine. Today the Runtime Worker owns its
lifecycle and reaches the isolated `omnivoice_env` service over loopback HTTP.
Architecture v2 later replaces that listener with a
bounded framed child-process protocol; the target is not current behavior yet.

Only the `original` voice style is accepted. Exactly 1.00x omits native speed
adjustment and remains the clone-quality reference. Other bounded speeds use
OmniVoice's native generation parameter; no local pitch shift, waveform DSP,
or time stretching is allowed. Non-1.00x output requires human listening before
any quality claim.

Voice streaming uses ordered WAV chunks, cancellation, playback
acknowledgements, and a complete-WAV fallback only when streaming fails before
the first chunk. The desktop starts `MediaPlayer.Play()` before notifying the
capacity-one subtitle event pump. Subtitle rendering is fail-open: a blocked,
failed, stale, or disabled subtitle path may drop text but cannot delay audio,
clear a newer cue, or fail playback.

Generation and OmniVoice do not own the GPU simultaneously. A voice failure is
reported as unavailable and never silently changes the saved engine/profile.

## Speech input and VTube Studio

Microphone capture is bounded 16 kHz mono PCM WAV. In WinUI Chat the primary
microphone button starts an explicit, user-controlled half-duplex conversation:
the local AudioGraph/VAD capture waits for one turn, normalizes variable
AudioGraph quantum sizes into the detector frame size, sends only a non-empty
transcript through the existing `/voice/transcribe` Google `vi-VN` path, submits
it to the active WebSocket session, and waits for the existing reply-completion
boundary before opening the microphone again. The button, typed Send, session
switch, and view shutdown cancel the loop; cancellation is idempotent and a
late STT result cannot create a prompt. The microphone remains closed while Ru
speaks, so speaker audio is not fed back into STT.

Raw PCM frames and transcripts are held only in memory for the active turn.
Audio leaves the machine for the explicitly requested STT operation and is not
persisted by Ru. A no-speech turn returns to Listening without sending an empty
prompt. The controller and VAD are covered by deterministic tests; physical
microphone accuracy, speaker output, and live VTube Studio behavior remain
manual acceptance gates.

VTube Studio hotkeys and expressions are resolved from the connected model's
current catalog. The runtime revalidates catalog codes before dispatch and
removes private action suffixes before chat, TTS, history, memory, and approved
examples. `VoiceVolume` is the lip-sync input. `LLM_MouthOpen` must not be
mapped or recreated. Live model connection, action, lip-sync, and subtitle
appearance remain user-run acceptance checks.

## Observation and desktop overlay

Screen observation is opt-in. With a Gemini Vision key and a supported hosted
generation backend, the private worker captures bounded JPEG frames, sends
them to the configured Gemini model, and publishes a reaction event containing
real counters, a reaction ID, and an acknowledgement boundary. WinUI polls the
bounded status contract and passes each event through `VoicePlaybackService`,
so MediaPlayer playback, subtitles, VTS speaking state, and acknowledgement
share the normal voice path. Missing credentials, stale events, or a busy chat
must report a skip/error and must not pretend that capture, reaction, or audio
playback occurred.

`RuDesktopOverlay` is an optional separate Spout2 receiver. It does not own
RuApp, VTube Studio, the model, VTS actions, or voice. Building the receiver may
use a local vcpkg cache; the cache is build infrastructure, not application
data or a distributable artifact.

## WinUI responsive shell

The WinUI shell has three presentation tiers based on client width: Wide at
1280 px and above, Compact from 900 through 1279 px, and Narrow below 900 px.
Wide keeps the resizable 180–400 px sidebar; Compact keeps labels with tighter
spacing and 180–320 px bounds; Narrow uses a 72 px icon rail and stacks page
controls. These transitions are presentation-only and do not call the backend,
restart polling, recreate list sources, or alter audio/VTS behavior.

Automated tests cover policy boundaries, XAML state markers, vertical-only list
surfaces, and build correctness. Visual resizing at representative window sizes
remains a user-run acceptance check.

The phase-one Chat redesign uses the same tiers. The shell and Chat use the
Editorial reading room presentation: the approved light mineral palette as the
baseline, a flat sidebar, and a centered document-style reading column.
Voice/VTS, Log, Diagnostics, Settings, and Memory retain their existing layout
in this phase while inheriting the active shared Light or Dark token set.
The application keeps Light as its fallback and MainWindow can request the
active theme on the shell root so default controls follow the same contrast.

The appearance action in the sidebar footer switches Light and Dark
synchronously. The selected appearance is UI-only state stored at
`%LOCALAPPDATA%\Ru\ui-preferences.json`; it is not an ASP.NET setting or model
configuration. Switching remains presentation-only for application behavior:
it does not recreate message or session sources, call the backend during
layout, alter WebSocket ordering, change STT, voice playback, subtitles, or
VTube Studio control. Automated source/XAML contracts and the WinUI build cover
structural correctness. Visual resizing and live audio/VTS behavior remain
manual acceptance checks.

The current Ru signature layer is also presentation-only: a quiet title-bar
wordmark, an editorial welcome eyebrow, message edge rules, and a GPU-accelerated
Composition visual field in the empty Chat state. The background features
gentle twinkling starlight points and periodic shooting star streaks rendered directly
via `Microsoft.UI.Composition` (`SpriteVisual`, `CompositionLinearGradientBrush` with
soft halo glow and core beam), runs only while `Messages.Count == 0`, and stops and
clears every visual element as soon as the first message exists. The composer keeps the
native TextBox caret/focus behavior without a decorative accent line; there is no ambient
welcome rectangle or extra surface layer. These elements do not add Chat content,
VTS/voice metadata, fake status indicators, or calls to the backend, WebSocket,
STT, voice, subtitle, VTS, or memory paths.

## Action execution kernel v1

Normal-chat and Agent-mode volume requests use the same path: the Worker makes
one non-visible structured selection, the Public Host opens only the scope in
the Broker capability, and RuAgentBroker executes and verifies the native
adapter. The Worker may render success only from a matching `succeeded` receipt
whose verification state is `verified`. Python does not own desktop side
effects.

Only `system.volume@1.0.0` is model-enabled in v1. File, terminal, notes, and
read-only system bindings remain Broker-internal; unbound connectors and
`app.open` are absent; skills are disabled. The GUI renders the actual
`agent.session` grant received from the Host.

## Persistence and privacy

ASP.NET Core owns settings, credentials, sessions/history, activity, structured
memory, and reviewed-example policy. Business records live in the canonical
`%LOCALAPPDATA%\Ru` location; the transactional migration from the legacy
location completed on 2026-08-17 (see
`docs/adr/0004-canonical-ru-data-location.md`). Windows Credential
Manager remains the secret store.

Logs, telemetry, manifests, and automated evidence may contain fixed event
types, counts, durations, hashes, and statuses only. They must not contain
credentials, bearer values, request bodies, user/session identifiers,
transcripts, raw audio, chat responses, or private VTS/voice metadata.

Technical diagnostics are structured JSONL under `RU_LOG_ROOT` (defaulting to
`%LOCALAPPDATA%\Ru\logs`) with bounded ASP.NET, Python-worker, and WinUI
writers. The HTTP boundary records fixed route categories and durations; the
diagnostics API rejects invalid/oversized queries and caps exports at 2 MB.
Activity Timeline writes continue through the private worker outbox and
ASP.NET ingestion path, with read-time re-sanitization for legacy records.

## Acceptance boundary

Automated tests can establish routing, protocol ordering, bounds, cancellation,
privacy filtering, persistence semantics, non-empty WAV structure, and process
lifecycle. They cannot establish speaker quality, physical microphone accuracy,
natural bilingual dialogue, or live VTube Studio behavior. Those remain
explicit user-run checks and must be reported as pending until performed.
