# Current runtime behavior

Status: operating contract for the Architecture v2 branch, 2026-08-13.

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

Microphone capture is bounded 16 kHz mono PCM WAV. Google recognition with the
`vi-VN` locale is the current experimental STT implementation; audio leaves the
machine for that explicitly requested operation and is not persisted by Ru.

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
Editorial reading room presentation: a light mineral canvas, a flat sidebar,
and a centered document-style reading column. Voice/VTS, Log, Diagnostics,
Settings, and Memory retain their existing layout in this phase.

The redesign is presentation-only. It does not recreate message or session
sources, call the backend during layout, alter WebSocket ordering, change STT,
voice playback, subtitles, VTube Studio control, or persist a new UI setting.
Automated source/XAML contracts and the WinUI build cover structural
correctness. Visual resizing and live audio/VTS behavior remain manual
acceptance checks.

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
