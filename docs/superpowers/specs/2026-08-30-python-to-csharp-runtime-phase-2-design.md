# Python-to-C# Runtime Migration Phase 2 Design

**Date:** 2026-08-30  
**Status:** Approved direction: Ru-owned runtime logic moves to C#; OmniVoice remains an isolated provider.

## Goal

Move the remaining production runtime/device orchestration from `src/ru_runtime`
to the managed C# runtime without rewriting the OmniVoice model or changing the
public ASP.NET Core, Agent, SSE, or desktop contracts. After the cutover, C#
owns Worker v1, runtime settings, cancellation, resource locks, voice
orchestration, speech recognition, VTube Studio actions, and screen-observation
policy. OmniVoice remains a provider behind a narrow typed boundary.

## Scope

- Implement the private Worker v1 server in `RuAgent.Runtime.Worker`.
- Move runtime settings/load/request behavior and lifecycle ownership to C#.
- Add focused C# adapters for voice, speech-to-text, VTube Studio, and Gemini
  observation.
- Keep OmniVoice model weights, reference audio, and the existing Python
  inference environment as provider data/runtime. The C# host may own a thin
  provider process, but provider code cannot own Ru sessions, persistence,
  policy, approvals, or public routes.
- Preserve the existing v1 envelope, binary audio framing, error codes,
  cancellation behavior, metadata redaction, and device acceptance boundary.
- Cut the default startup over only after contract, parity, privacy, and manual
  device gates pass. Keep the explicit Python compatibility selector until the
  later retirement phase.

## Non-goals

- Rewriting or retraining OmniVoice, changing its weights, or promising native
  inference before an export/backend feasibility proof.
- Removing Python from test/benchmark tooling or from the temporary isolated
  OmniVoice provider process.
- Changing public routes, UI semantics, Agent system-prompt composition,
  workspace policy, approvals, credentials, or the future OS sandbox project.
- Adding extensions, plugins, MCP, subagents, or LSP.

## Architecture

The managed Worker process exposes both private protocol surfaces. It may bind
the Agent and Device URLs on two loopback ports in one process so the existing
startup descriptor and client options remain stable:

```text
RuBackend / RuApp / RuAgent.Cli
        -> Public Host
        -> RuAgent.Runtime.Worker (C#; /worker/v2 and /worker/v1)
             -> Runtime.Core contracts and locks
             -> Runtime.Adapters
                  -> C# HTTP/WebSocket provider clients
                  -> isolated OmniVoice provider process (temporary)
                  -> Google STT / Gemini Vision
                  -> VTube Studio WebSocket
```

`RuAgent.Protocol` remains the single wire-contract owner. `Runtime.Core`
contains bounded value objects, lifecycle state, redaction, and interfaces; it
does not depend on sockets, processes, audio devices, or ASP.NET. The Worker
owns protocol validation, request tasks, cancellation, sequencing, and safe
error mapping. Adapters perform external I/O and never leak raw provider
exceptions or private device metadata across the Worker boundary.

## Managed interfaces

The following interfaces are the stable seams for deterministic tests and
provider replacement:

```csharp
public interface IVoiceProvider
{
    Task<VoiceAudio> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<VoiceChunk> StreamAsync(
        VoiceRequest request,
        CancellationToken cancellationToken);
}

public interface ISpeechRecognizer
{
    Task<string> RecognizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken);
}

public interface IVtubeStudioClient
{
    Task<VtsCatalog> ReadCatalogAsync(CancellationToken cancellationToken);
    Task TriggerAsync(
        IReadOnlyList<string> catalogCodes,
        CancellationToken cancellationToken);
}

public interface IObservationProvider
{
    Task<VisionObservation?> AnalyzeAsync(
        ObservationRequest request,
        CancellationToken cancellationToken);
}
```

All request and result records enforce the existing byte, text, count, timeout,
and identifier limits at construction or at the adapter boundary. Voice audio
is mono 24 kHz PCM/WAV as required by the current contract. Provider errors map
to fixed codes (`voice_unavailable`, `voice_failed`, `speech_no_match`,
`vts_unavailable`, `vts_action_failed`, `observation_skipped`, and bounded
transport/configuration variants); raw response bodies, API keys, prompts,
paths, transcripts, VTS codes, and audio bytes do not enter diagnostics or
durable history.

## OmniVoice boundary

`OmniVoiceHttpProvider` is a C# adapter over the existing isolated service. It
validates a loopback-only base URI, sends bounded `/warmup`, `/tts`, and
`/tts_stream` requests, validates content type/WAV headers and chunk ordering,
and maps timeout/cancellation deterministically. It owns no model state beyond
the provider lease and does not expose Flask or Python types to `Runtime.Core`.

During the compatibility window, the process manager may start the existing
`omnivoice_env` interpreter with the repository's thin `omnivoice_server.py`
shim. The shim is provider implementation only; C# owns startup intent,
timeouts, cancellation, GPU/audio ownership, and shutdown. A later native
provider can implement `IVoiceProvider` without changing Worker v1 or desktop
callers.

## Worker v1 behavior

The C# Worker v1 server must preserve the existing `hello`, `runtime.load`,
`runtime.request`, `voice.start`, `vts.catalog`, `vts.action`, and health
envelopes, including strict UTF-8, request IDs, correlation IDs, sequence
numbers, binary-frame announcements, and the `4400`/`4401`/`4404` close
semantics. Request tasks are bounded per connection, cancellation is
cooperative end-to-end, and cleanup awaits provider ownership before releasing
GPU/audio/VTS locks.

Runtime settings are validated and applied in C# using the existing trusted
settings/credential stores. No credential can arrive through a v1 request
payload. Runtime requests continue to proxy only the allowlisted private
routes needed by the desktop; arbitrary public HTTP proxying is rejected.

## Adapter responsibilities

- **Voice:** preserve voice selection, `original` style, native speed bounds,
  ordered streaming, playback acknowledgements, complete-WAV fallback, and
  subtitle fail-open behavior. `MediaPlayer.Play()` remains before subtitle
  notification in WinUI.
- **Speech:** preserve bounded 16 kHz mono WAV capture, Google `vi-VN` request
  shape, no-match/error mapping, and no persistence of raw audio/transcripts.
- **VTube Studio:** replace the Python `pyvts` client with a bounded C#
  `ClientWebSocket` implementation. Re-read and validate the current catalog
  before actions, preserve reconnect/authentication behavior, and strip private
  action markers before TTS/history/memory.
- **Observation:** replace Python `urllib` with a C# `HttpClient` Gemini
  adapter. Preserve the model cascade, JSON-only generation config, bounded
  screenshot bytes, redaction, novelty/confidence clamping, and skip/error
  semantics when credentials or the Agent lock are unavailable.

## Rollout

1. Add the typed records/interfaces and focused contract tests without changing
   startup.
2. Add the C# v1 server and fake-provider integration tests; run it on an
   opt-in compatibility path while Python v1 remains default.
3. Add adapters one at a time with Python golden fixtures for validation,
   privacy, redaction, cancellation, and binary/audio bounds.
4. Run automated suites plus explicit Windows microphone, playback, OmniVoice,
   VTS, and observation acceptance. These live checks cannot be inferred from
   unit or protocol tests.
5. Make C# the default v1 endpoint only after every supported device scenario
   passes. Keep `PythonCompatibility` as an explicit rollback setting.
6. Update current architecture, operations, build, and verification docs. Do
   not delete Python production modules until the separate retirement phase has
   import/startup evidence.

## Acceptance gates

- Worker v1 contract fixtures round-trip and reject malformed/oversized input.
- Python and C# deterministic fixtures agree on selected settings, voice
  controls, VTS catalog filtering, observation parsing, and bounded errors.
- Cancellation releases every request, GPU/audio lock, and provider process.
- Privacy scans find no credentials, raw prompts, transcripts, audio, paths,
  VTS metadata, or provider bodies in visible/durable outputs.
- Public route count remains `56 HTTP + 1 WebSocket`.
- Heavy Python and .NET suites run sequentially; no live device claim is made
  until manual Windows acceptance is recorded.

