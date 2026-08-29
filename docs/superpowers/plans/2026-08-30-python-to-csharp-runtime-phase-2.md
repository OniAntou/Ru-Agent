# Managed Runtime Phase 2 Implementation Plan

> **For agentic workers:** Execute this plan inline in the current workspace. Keep each task's red/green test cycle and commit isolated; do not delegate to subagents.

**Goal:** Move Ru-owned Worker v1, runtime/device orchestration, voice/STT/VTS/observation adapters, and lifecycle ownership to C# while keeping OmniVoice as an isolated provider and preserving every public contract.

**Architecture:** One published `RuAgent.Runtime.Worker` process listens on the existing Agent and Device loopback ports and serves `/worker/v2` and `/worker/v1`. Pure bounded device records and leases live in a small `RuAgent.Runtime.Core` project; external I/O lives in `RuAgent.Runtime.Adapters`. The OmniVoice model and Python inference environment remain behind a C# provider client/process boundary.

**Tech Stack:** .NET 10, ASP.NET Core WebSockets, `HttpClient`, `ClientWebSocket`, `System.Text.Json`, existing Windows process/job APIs, xUnit, and Python reference fixtures.

## Global Constraints

- Preserve the public contract at `56 HTTP + 1 WebSocket`.
- Preserve Worker v1 envelope names, sequence rules, binary framing, error codes, and close semantics.
- C# owns Ru business logic, policy, lifecycle, cancellation, locks, and redaction; provider processes never own persistence, sessions, approvals, or public routes.
- OmniVoice weights, reference audio, and `omnivoice_env` remain provider assets; do not retrain or rewrite the model.
- No raw prompts, transcripts, audio, credentials, paths, VTS metadata, or provider bodies may enter visible history, durable activity, or diagnostics.
- Enforce limits before provider invocation and await cancellation before releasing a resource lock.
- No automatic Python fallback. `PythonCompatibility` remains an explicit trusted rollback mode until retirement evidence exists.
- Run heavyweight Python and .NET suites sequentially.
- Do not modify unrelated dirty `TODO.md` or historical `docs/superpowers` files.
- Real microphone, speaker, OmniVoice, VTube Studio, and observation behavior remains a manual Windows acceptance gate.

---

### Task 1: Promote Worker v1 contracts into the shared protocol assembly

**Files:** Create `backend-dotnet/RuAgent.Protocol/Harness/WorkerV1Contracts.cs`, `WorkerV1Envelope.cs`, `WorkerV1EnvelopeCodec.cs`; test `backend-dotnet/RuAgent.Protocol.Tests/WorkerV1EnvelopeTests.cs`.

**Interfaces:** `WorkerV1Envelope(int Version, string Id, string Type, long? Sequence, string? CorrelationId, JsonObject Payload)`; `WorkerV1EnvelopeCodec.Parse(string)` and `Serialize(WorkerV1Envelope)`.

- [x] Write tests for valid requests, `correlationId` normalization, unsupported version, unknown type, missing/non-negative sequence, invalid IDs, and frame-size rejection.
- [x] Run `dotnet test D:\LLM\backend-dotnet\RuAgent.Protocol.Tests\RuAgent.Protocol.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WorkerV1Envelope"`; expected FAIL because types do not exist.
- [x] Implement protocol `1`, ID limit `128`, correlation limit `64`, frame limit `8 MiB`, exact known/ordered type sets from `src/ru_runtime/worker/v1/protocol.py`, strict JSON object payloads, and canonical `correlation_id` serialization.
- [x] Run the same filter in Release with `--no-restore`; expected PASS.
- [x] Commit `git add backend-dotnet/RuAgent.Protocol/Harness/WorkerV1Contracts.cs backend-dotnet/RuAgent.Protocol/Harness/WorkerV1Envelope.cs backend-dotnet/RuAgent.Protocol/Harness/WorkerV1EnvelopeCodec.cs backend-dotnet/RuAgent.Protocol.Tests/WorkerV1EnvelopeTests.cs; git commit -m "feat: add shared Worker v1 protocol contracts"`.

### Task 2: Add the managed Worker v1 connection server

**Files:** Create `backend-dotnet/RuAgent.Runtime.Worker/WorkerV1Options.cs`, `WorkerV1Reply.cs`, `WorkerV1Server.cs`, `WorkerV1RequestRouter.cs`; modify `backend-dotnet/RuAgent.Runtime.Worker/Program.cs`; test `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV1ServerContractTests.cs`.

**Interfaces:** `IWorkerV1RequestRouter.RouteAsync(string type, JsonObject payload, CancellationToken)` returns `IAsyncEnumerable<WorkerV1Reply>`; `WorkerV1Reply(string Type, JsonObject Payload, ReadOnlyMemory<byte> Binary)`; `WorkerV1Server.HandleAsync(HttpContext)` serves `/worker/v1`.

- [x] Write in-process WebSocket tests for hello/health, wrong token `4401`, wrong path `4404`, malformed JSON, duplicate/in-flight requests, ordered sequence, binary announcement/attachment, cancellation, and peer cleanup.
- [x] Run `dotnet test D:\LLM\backend-dotnet\RuAgent.Runtime.Worker.Tests\RuAgent.Runtime.Worker.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WorkerV1"`; expected FAIL.
- [x] Mirror Python lifecycle: authenticate the first text hello with fixed-time comparison; advertise `runtime`, `voice`, `vts`, `observation`; keep one send semaphore; track request cancellation sources; await every request in `finally`; map failures only to bounded codes.
- [x] Extend `Program.cs` to accept `--device-port` and `RU_DEVICE_WORKER_TOKEN`, listen on both loopback URLs, and route `/worker/v2` to the existing server and `/worker/v1` to the new server. Initial v1 handlers may return typed unavailable errors until later tasks bind adapters.
- [x] Run all Worker tests sequentially in Release; expected PASS.
- [x] Commit `git add backend-dotnet/RuAgent.Runtime.Worker backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV1ServerContractTests.cs; git commit -m "feat: add managed Worker v1 server"`.

### Task 3: Create focused managed runtime/device contracts and leases

**Files:** Create project `backend-dotnet/RuAgent.Runtime.Core/RuAgent.Runtime.Core.csproj`, records under `Contracts/`, `Lifecycle/RuntimeLeaseCoordinator.cs`, project `backend-dotnet/RuAgent.Runtime.Core.Tests/RuAgent.Runtime.Core.Tests.csproj`, and `RuntimeContractTests.cs`; add project references from `RuAgent.Runtime.Adapters`, `RuAgent.Runtime.Worker`, and their test projects.

**Interfaces:** `VoiceRequest`, `VoiceAudio`, `VoiceChunk(string State, long Sequence, ReadOnlyMemory<byte> Audio)`, `SpeechRequest`, `VtsCatalog`, `ObservationRequest`, `VisionObservation`, `RuntimeSettingsSnapshot`, `ResourceKind`, and `RuntimeLeaseCoordinator.AcquireAsync(ResourceKind, CancellationToken)`.

- [x] Write failing tests for UTF-8/text/audio/count limits, `original` voice style, mono 24 kHz voice audio, mono 16 kHz STT audio, at most 512 VTS codes, at most three action codes, numeric observation clamping, and second concurrent lease waiting until disposal.
- [x] Run the new core test project; expected FAIL because the project does not exist.
- [x] Implement BCL-only immutable records and idempotent async leases. Keep process, socket, ASP.NET, JSON-node, and device APIs out of this project.
- [x] Run core tests plus Release builds of Adapters and Worker; expected PASS.
- [x] Commit `git add backend-dotnet/RuAgent.Runtime.Core backend-dotnet/RuAgent.Runtime.Core.Tests backend-dotnet/RuAgent.Runtime.Adapters/RuAgent.Runtime.Adapters.csproj backend-dotnet/RuAgent.Runtime.Worker/RuAgent.Runtime.Worker.csproj backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj; git commit -m "feat: add managed runtime device contracts"`.

### Task 4: Port runtime settings and private request routing

**Files:** Create `backend-dotnet/RuAgent.Runtime.Adapters/Runtime/ManagedRuntimeSettingsService.cs` and `PrivateRuntimeRequestRouter.cs`; modify `WorkerV1RequestRouter.cs`; tests `RuntimeV1HandlerTests.cs` and `backend-dotnet/RuBackend.Tests/Worker/ManagedRuntimeClientTests.cs`.

**Interfaces:** `RuntimeSettingsInput(string Backend, string ModelName, string? BaseUrl, string ApiVariant, bool KiloReasoningEnabled)`; `ManagedRuntimeSettingsService.LoadAsync(RuntimeSettingsInput, CancellationToken)`; `PrivateRuntimeRequest(string Method, string PathAndQuery, string? ContentType, ReadOnlyMemory<byte> Body)`; and `PrivateRuntimeRequestRouter.InvokeAsync(PrivateRuntimeRequest, CancellationToken)`.

- [x] Test persisted backend/model/base URL round-trip, rollback on load failure, secret-like request keys returning `invalid_request`, allowlisted settings/status/model paths, and rejection of unallowlisted paths without provider invocation.
- [x] Run the focused Worker/Backend filters; expected FAIL because managed v1 settings are absent.
- [x] Keep persistence in the trusted Host settings/credential stores rather than duplicating it in the worker. Apply the existing backend mapping and rollback semantics from `runtime_services.py`. Require an explicit `binary=true` response announcement before returning bytes.
- [x] Run focused Release tests and existing `RuntimeSettingsLoader` tests; expected PASS.
- [x] Commit `git add backend-dotnet/RuAgent.Runtime.Adapters/Runtime backend-dotnet/RuAgent.Runtime.Worker/WorkerV1RequestRouter.cs backend-dotnet/RuBackend.Tests/Worker/ManagedRuntimeClientTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/RuntimeV1HandlerTests.cs; git commit -m "feat: own runtime settings in managed Worker"`.

### Task 5: Port voice orchestration while keeping OmniVoice isolated

**Files:** Create `backend-dotnet/RuAgent.Runtime.Adapters/Audio/OmniVoiceProviderOptions.cs`, `OmniVoiceHttpProvider.cs`, `OmniVoiceProcessController.cs`, `VoiceChunker.cs`, `VoiceStreamCoordinator.cs`; bind in `WorkerV1RequestRouter.cs`; tests `OmniVoiceProviderTests.cs`, `VoiceStreamCoordinatorTests.cs`, and `VoiceV1HandlerTests.cs`.

**Interfaces:** `OmniVoiceHttpProvider` calls `/warmup`, `/release`, `/tts`, `/tts_stream`; `OmniVoiceProcessController.StartAsync/StopAsync`; `VoiceStreamCoordinator.StreamAsync(VoiceRequest, CancellationToken)` returns `IAsyncEnumerable<VoiceChunk>`.

- [x] Write failing tests with fake `HttpMessageHandler` and fake process handle for loopback URI validation, request serialization, timeout/cancellation, empty/invalid WAV, sentence ordering, first-audio ordering, pre-first-chunk complete-WAV fallback, and lock release.
- [x] Run the focused Adapter/Worker filters; expected FAIL.
- [x] Implement C# HTTP validation and bounded error mapping. Keep `omnivoice_env`, model files, and `omnivoice_server.py` untouched; the controller may launch that thin provider shim with hidden Windows startup and owns only its process lifetime. Hold the GPU/audio lease for the full provider call and never log text/audio.
- [x] Run focused C# voice tests plus Python `tests/test_voice_stream.py`, `test_voice_pacing.py`, and `test_omnivoice_tts_server.py` sequentially; expected PASS.
- [x] Commit `git add backend-dotnet/RuAgent.Runtime.Adapters/Audio backend-dotnet/RuAgent.Runtime.Worker/WorkerV1RequestRouter.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/OmniVoiceProviderTests.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/VoiceStreamCoordinatorTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/VoiceV1HandlerTests.cs; git commit -m "feat: move voice orchestration behind managed OmniVoice provider"`.

### Task 6: Port speech-to-text and audio validation

**Files:** Create `backend-dotnet/RuAgent.Runtime.Adapters/Speech/GoogleSpeechRecognizer.cs` and `WavAudioValidator.cs`; bind in `WorkerV1RequestRouter.cs`; tests `GoogleSpeechRecognizerTests.cs` and `SpeechV1HandlerTests.cs`.

**Interfaces:** `GoogleSpeechRecognizer.RecognizeAsync(SpeechRequest, CancellationToken)` and `WavAudioValidator.Validate(ReadOnlySpan<byte>, int expectedRate, int channels)`.

- [ ] Test valid 16 kHz mono PCM WAV, malformed RIFF/non-PCM/wrong rate/empty/oversized audio, whitespace normalization, no-match mapping, timeout/5xx mapping, and secret/response-body privacy.
- [ ] Run focused Adapter/Worker filters; expected FAIL.
- [ ] Implement language `vi-VN`, the existing 15-second budget, normalized non-empty transcripts, and no persistence. Use injected transport; if the anonymous `speech_recognition` endpoint cannot be reproduced with a stable C# request, keep it as an explicitly isolated provider and document that fact instead of silently changing the contract.
- [ ] Run focused C# tests plus Python STT references sequentially; expected PASS.
- [ ] Commit `git add backend-dotnet/RuAgent.Runtime.Adapters/Speech backend-dotnet/RuAgent.Runtime.Worker/WorkerV1RequestRouter.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/GoogleSpeechRecognizerTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/SpeechV1HandlerTests.cs; git commit -m "feat: move speech recognition boundary to managed runtime"`.

### Task 7: Port VTube Studio catalog/actions

**Files:** Create `backend-dotnet/RuAgent.Runtime.Adapters/Devices/VtubeStudioClient.cs` and `VtubeStudioCatalog.cs`; bind in `WorkerV1RequestRouter.cs`; tests `VtubeStudioClientTests.cs` and `VtsV1HandlerTests.cs`.

- [ ] Use an in-process WebSocket server to test authentication, fragmentation/size bounds, reconnect, model-change invalidation, catalog filtering, duplicate/unknown actions, and safe error mapping.
- [ ] Run the focused Adapter filter; expected FAIL.
- [ ] Implement the existing VTube Studio authentication/message flow with `ClientWebSocket`; revalidate the current catalog immediately before each action; accept at most three codes; strip private suffixes before any TTS/history/memory path.
- [ ] Run focused C# tests plus Python VTS reference tests sequentially; expected PASS.
- [ ] Commit `git add backend-dotnet/RuAgent.Runtime.Adapters/Devices backend-dotnet/RuAgent.Runtime.Worker/WorkerV1RequestRouter.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/VtubeStudioClientTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/VtsV1HandlerTests.cs; git commit -m "feat: move VTube Studio adapter to managed runtime"`.

### Task 8: Port Gemini observation and privacy filtering

**Files:** Create `backend-dotnet/RuAgent.Runtime.Adapters/Observation/GeminiVisionProvider.cs` and `VisionObservationParser.cs`; bind in `WorkerV1RequestRouter.cs`; tests `GeminiVisionProviderTests.cs` and `ObservationV1HandlerTests.cs`.

- [ ] Test model fallback, model-specific sampling config, fenced JSON, malformed candidates, missing key/image, byte/field bounds, redaction, and provider-body privacy using fake HTTP handlers.
- [ ] Run focused Adapter/Worker filters; expected FAIL.
- [ ] Implement the current `generateContent` JSON shape and model cascade with `HttpClient`; send API key only in the header; clamp numeric fields to `0..1`; return `observation_skipped` for missing key/busy lock and `observation_failed` for bounded provider failures.
- [ ] Run focused C# tests plus Python observation references sequentially; expected PASS.
- [ ] Commit `git add backend-dotnet/RuAgent.Runtime.Adapters/Observation backend-dotnet/RuAgent.Runtime.Worker/WorkerV1RequestRouter.cs backend-dotnet/RuAgent.Runtime.Adapters.Tests/GeminiVisionProviderTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/ObservationV1HandlerTests.cs; git commit -m "feat: move Gemini observation adapter to managed runtime"`.

### Task 9: Compose managed handlers and preserve backend clients

**Files:** Create `backend-dotnet/RuAgent.Runtime.Worker/ManagedWorkerComposition.cs`; modify `Program.cs`, Worker project references, `backend-dotnet/RuBackend/Worker/WorkerConnection.cs`, and `LoopbackWorkerClient.cs`; tests `ManagedWorkerCompositionTests.cs` and existing loopback client tests.

- [ ] Test that v1/v2 share one lease coordinator but use distinct tokens, v1 receives no provider bootstrap, and all existing client methods parse unchanged envelopes/binary frames.
- [ ] Run focused Worker/Backend filters; expected FAIL.
- [ ] Compose settings, voice, STT, VTS, observation, and runtime handlers through dependency injection; pass `--device-port`, `RU_DEVICE_WORKER_TOKEN`, `RU_PROJECT_ROOT`, and `RU_LOG_ROOT`; start OmniVoice lazily and stop it on worker shutdown.
- [ ] Run all Worker tests and backend worker/runtime/speech tests sequentially; expected PASS.
- [ ] Commit `git add backend-dotnet/RuAgent.Runtime.Worker backend-dotnet/RuBackend/Worker backend-dotnet/RuAgent.Runtime.Worker.Tests backend-dotnet/RuBackend.Tests/Worker backend-dotnet/RuBackend.Tests/Speech; git commit -m "feat: compose managed v1 runtime adapters"`.

### Task 10: Cut managed v1 into startup with explicit rollback

**Files:** Modify `backend-dotnet/RuAgent.Hosting/WorkerBrokerLaunchPlan.cs`, `WorkerBrokerProcessManager.cs`, `backend-dotnet/RuBackend/Program.cs`, hosting tests, and add startup tests under `backend-dotnet/RuBackend.Tests`.

- [ ] Write failing topology tests requiring one `dotnet-agent-worker` process with both ports/tokens in Dotnet mode, no Python Device Worker there, and the old Python topology only in `PythonCompatibility` mode. Readiness must probe Agent, Device, and Broker ports; failure must kill every child and remove only owned artifacts.
- [ ] Run focused Hosting/Backend filters; expected FAIL because Dotnet mode starts Python v1.
- [ ] Change the managed launch arguments to `--host 127.0.0.1 --port {agentPort} --device-port {devicePort}`, pass both worker tokens/project/log roots, and keep Python compatibility launch unchanged. Preserve public descriptor/routes and separate client options.
- [ ] Run Hosting/Backend tests and `D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py`; expected PASS and `56 HTTP + 1 WebSocket`.
- [ ] Commit the exact Hosting/Backend files with `git commit -m "feat: make managed Worker the default device runtime"`.

### Task 11: Add parity, privacy, manual acceptance, and current documentation

**Files:** Create `tests/test_phase2_managed_device_parity.py`, `tests/fixtures/python-csharp-phase2-device-baseline.json`, managed Phase2 integration/parity tests; modify `build.bat`, `docs/README.md`, `docs/architecture.md`, `docs/operations/runtime-behavior.md`, `docs/operations/python-runtime-boundary.md`, `docs/operations/verification.md`, and `backend-dotnet/README.md`; add `tests/test_phase2_build_contract.py`.

- [ ] Write failing parity tests comparing only type/sequence, settings, voice controls/chunk order, stable errors, STT normalization, VTS code sets, observation bounds, cancellation, and redaction booleans; scan logs/frames for forbidden raw values.
- [ ] Run the new Python/C# filters; expected FAIL until fixtures and harnesses exist.
- [ ] Implement fake-provider deterministic evidence, build publication of `RuAgent.Runtime.Core` and managed Worker, and current docs stating C# owns device orchestration while OmniVoice is an isolated provider and Python compatibility is explicit.
- [ ] Run sequentially: `git diff --check`; `call D:\LLM\build.bat`; route checker; all Protocol/Core/Adapters/Worker/Hosting/Backend Release tests; full Python pytest; ruff; mypy. Require exit code `0` and route count `56 HTTP + 1 WebSocket` before any completion claim.
- [ ] Run manual Windows checks with the user for microphone, no-speech, OmniVoice stream/cancel, playback/subtitle order, VTS catalog/action/reconnect, and opt-in Gemini. Record only pass/fail, duration, and bounded codes.
- [ ] Commit only current build/docs/evidence files with `git commit -m "docs: record managed device runtime ownership"`.

## Completion checklist

- [ ] Worker v1 contracts/server pass strict protocol tests.
- [ ] Runtime settings, leases, cancellation, and private routing are C# owned.
- [ ] Voice orchestration is C# managed while OmniVoice remains isolated.
- [ ] STT, VTS, and Gemini adapters have deterministic transport/parity/privacy tests.
- [ ] Dotnet startup owns the managed Worker on both private ports; Python compatibility is explicit only.
- [ ] Public routes remain `56 HTTP + 1 WebSocket`; UI and privacy policy are unchanged.
- [ ] Current docs/build are updated; no stale current ownership claim remains.
- [ ] Manual Windows acceptance is recorded separately from automated evidence.
- [ ] Python production modules are retained until a separate Phase 3 retirement plan has import/startup evidence.
