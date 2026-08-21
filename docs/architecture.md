# Ru current architecture

Status: current ownership plus accepted architecture-v2 targets, 2026-08-13.

Operational invariants for generation, voice, STT, VTS, observation, and live
acceptance are maintained in
[`operations/runtime-behavior.md`](operations/runtime-behavior.md).

## Process topology

RuApp owns the desktop lifecycle. It creates separate per-run desktop and
worker secrets, starts the private Runtime Worker on loopback port 8005, then
starts the ASP.NET Core Public Host on port 8000. Both children enter one
Windows kill-on-close Job Object. Startup succeeds only after public readiness;
shutdown or startup failure terminates the owned process tree.

The current process flow is:

```text
RuApp
  -> ASP.NET Core Public Host :8000
       -> direct business and persistence services
       -> authenticated Runtime Worker protocol v1 :8005
            -> hosted and external OpenAI-compatible provider adapters
            -> Google STT, VTube Studio, and observation implementations
            -> OmniVoice Flask service :5052 in omnivoice_env
```

The accepted target flow, implemented by later architecture-v2 plans, is:

```text
RuApp -> ASP.NET Core public contract -> typed worker protocol v2 -> Runtime Worker
Runtime Worker -> model adapters / Voice Engine / STT / VTS / observation
```

The typed worker protocol v2 and framed Voice Engine are accepted decisions,
not claims about the currently running v1/Flask implementation.

## Ownership

| Owner | Responsibilities |
| --- | --- |
| RuApp | Desktop views, playback, microphone capture, public client calls, child lifecycle, and user-visible state. |
| ASP.NET Core Public Host | Authentication, limits, all 41 HTTP and 2 WebSocket routes, settings transactions, credentials, sessions, history, activity, structured memory, chat ordering, public error mapping, and metadata filtering. |
| Runtime Worker | Provider adapters and device implementations that materially require Python: generation, STT execution, VTS, observation, and current OmniVoice coordination. |
| Voice Engine | OmniVoice model lifecycle and native WAV synthesis inside its isolated environment; currently reached through Flask and replaced by framed process I/O in a later plan. |

Unknown public paths stop in ASP.NET Core. Python has no public HTTP fallback,
forwarding catch-all, route ownership selector, or alternate host mode.

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
41 HTTP method/path pairs and 2 WebSocket routes (`/chat` and
`/voice/stream`). REST and WebSocket clients authenticate with the desktop
secret. ASP.NET Core maps every public response and error.

Protocol v1 is a private authenticated loopback WebSocket. Its chat, memory,
voice, STT, VTS, and observation operations are versioned messages, but the
`runtime.request`/`runtime.response` seam still carries HTTP-shaped method,
path, content type, status, and body fields. Protocol v2 removes those fields
in favor of typed domain commands, capabilities, bounded payloads, ordering,
cancellation, and stable worker errors.

Public `/voice/stream` audio is carried as binary WAV frames. In the current
implementation the Runtime Worker reaches OmniVoice over loopback HTTP on port
5052. The accepted Voice Engine target uses bounded stdin/stdout control and
binary frames, keeps native-speed audio, and exposes no network listener.

## Data ownership

ASP.NET Core owns business persistence. Settings, history, sessions, activity,
and the VTS token default to the canonical `%LOCALAPPDATA%\Ru` location;
structured memory defaults to `data/conversations`, with `RU_DATA_ROOT`
available for isolated runs. Secrets are stored through Windows Credential
Manager rather than JSON files.

The transactional migration from `~\.minigpt` completed on 2026-08-17 with
copy, validation, hashing, atomic activation, readability checks, and
credential migration performed before legacy records were retired; see
[ADR 0004](adr/0004-canonical-ru-data-location.md).

## Privacy boundary

Only data needed for a requested operation crosses a private boundary. Visible
chat text may enter generation and approved persistence, but transport-only
voice controls and private VTS actions, motions, overlays, timings, segment
identifiers, and model-control metadata must not enter chat, TTS text, history,
memory, reviewed examples, cleanup manifests, or logs.

Audio remains binary and manifests contain metadata only. Automated checks may
record statuses, timings, counts, event types, and cleanup state; they never
record credentials, bearer tokens, request bodies, session identifiers,
transcripts, audio bytes, or private VTS metadata.
