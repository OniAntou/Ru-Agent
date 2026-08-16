# Current runtime behavior

Status: operating contract for the Architecture v2 branch, 2026-08-13.

This document keeps current runtime decisions separate from implementation
history. Where automated and live evidence differ, the narrower claim wins.

## Conversation and generation

The local desktop default is `LFM2.5-2.6B-Q4_K_M.gguf` through the private
llama.cpp generation kernel. Hosted OpenCode Zen and Kilo Gateway providers are
optional adapters. Switching models must preserve one-owner GPU lifecycle,
bounded context, cancellation, streamed visible-text normalization, and public
error mapping by ASP.NET Core.

Ru mirrors Vietnamese, English, and mixed-language turns. In Vietnamese Ru
calls the user `anh` and itself `em`. Chat contains no Unicode emoji or ASCII
emoticons. Search commands, model controls, voice pace, VTS actions, segment
identifiers, and transport timings are private metadata and must be removed
before display, TTS, history, memory, logs, or reviewed examples.

The model-neutral V5 evaluation suite is exposed by
`scripts/evaluate_ru_model.py` and `configs/ru_judge_eval.json`. Deterministic
hard rules run without an external judge. Judge scores are supporting evidence;
they do not replace bilingual conversation and context review.

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

Local desktop capture and the former local vision provider are disabled. The
ASP.NET Core observation contract remains bounded and reports `disabled` when
no replacement provider is configured. It must not pretend that capture or a
reaction occurred.

`RuDesktopOverlay` is an optional separate Spout2 receiver. It does not own
RuApp, VTube Studio, the model, VTS actions, or voice. Building the receiver may
use a local vcpkg cache; the cache is build infrastructure, not application
data or a distributable artifact.

## Persistence and privacy

ASP.NET Core owns settings, credentials, sessions/history, activity, structured
memory, and reviewed-example policy. Business records live in the canonical
`%LOCALAPPDATA%\Ru` location; the transactional migration from the legacy
location completed on 2026-08-17 (see
`docs/validation/plan3-task6-migration-evidence.md`). Windows Credential
Manager remains the secret store.

Logs, telemetry, manifests, and automated evidence may contain fixed event
types, counts, durations, hashes, and statuses only. They must not contain
credentials, bearer values, request bodies, user/session identifiers,
transcripts, raw audio, chat responses, or private VTS/voice metadata.

## Acceptance boundary

Automated tests can establish routing, protocol ordering, bounds, cancellation,
privacy filtering, persistence semantics, non-empty WAV structure, and process
lifecycle. They cannot establish speaker quality, physical microphone accuracy,
natural bilingual dialogue, or live VTube Studio behavior. Those remain
explicit user-run checks and must be reported as pending until performed.
