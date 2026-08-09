# Ru ASP.NET Core Runtime Slices and FastAPI Decommission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Move the remaining voice, VTS, observation, status, and model-runtime route ownership to ASP.NET Core worker adapters, then stop launching FastAPI while retaining Python runtime implementations.

**Architecture:** ASP.NET owns every public HTTP/WebSocket route and delegates only runtime-dependent operations through IWorkerClient. The Python worker owns model/TTS/STT/VTS/observation implementations behind the versioned loopback protocol. The compatibility forwarder and FastAPI process remain available until the final route-matrix, real-audio, process-cleanup, and rollback gates pass.

**Tech Stack:** ASP.NET Core WebSockets, C#, System.Text.Json, Python asyncio/websockets, xUnit, pytest, WinUI process manager, real OmniVoice loopback service, and the existing live compatibility harness.

## Global Constraints

- Preserve all 38 HTTP method/path pairs and both WebSocket paths from the route matrix.
- Preserve public WinUI JSON shapes, status codes, auth, request-size limits, WebSocket close behavior, and voice control/binary ordering.
- Preserve real OmniVoice audio confirmation: a voice control frame must be followed by non-empty binary audio.
- Preserve VTS metadata privacy and current disabled observation behavior when no provider is configured.
- Keep model, TTS/STT, VTS, and observation implementation in Python; ASP.NET owns transport and orchestration only.
- Do not add a second chat state machine, voice playback queue, VTS scheduler, or observation lifecycle.
- Use one worker process and one ASP.NET process under the existing Job Object; fail closed on startup/readiness/cleanup errors.
- FastAPI rollback remains selectable until final acceptance; no automatic fallback and no dual writers.
- Do not log tokens, API keys, chat text, audio bytes, private VTS metadata, or screen content.
- Run Python, .NET, GUI, build, live audio, and benchmark checks sequentially.
- Update README.md, backend-dotnet/README.md, route matrix, validation evidence, and migration docs after each runtime slice.

---

## File Map

Create:

- backend-dotnet/RuBackend/Runtime/RuntimeModels.cs — public response and private worker DTOs.
- backend-dotnet/RuBackend/Runtime/VoiceEndpoints.cs — direct voice HTTP endpoints.
- backend-dotnet/RuBackend/Runtime/VoiceStreamEndpoint.cs — direct /voice/stream relay to IWorkerClient.
- backend-dotnet/RuBackend/Runtime/VtsEndpoints.cs — direct VTS HTTP endpoints.
- backend-dotnet/RuBackend/Runtime/ObservationEndpoints.cs — direct observation endpoints.
- backend-dotnet/RuBackend/Runtime/StatusEndpoints.cs — status/models/readiness endpoints.
- backend-dotnet/RuBackend.Tests/Runtime/VoiceEndpointsTests.cs
- backend-dotnet/RuBackend.Tests/Runtime/VoiceStreamEndpointTests.cs
- backend-dotnet/RuBackend.Tests/Runtime/VtsEndpointsTests.cs
- backend-dotnet/RuBackend.Tests/Runtime/ObservationEndpointsTests.cs
- backend-dotnet/RuBackend.Tests/Runtime/StatusEndpointsTests.cs
- tests/fixtures/runtime-route-contract.json

Modify:

- backend-dotnet/RuBackend/Worker/IWorkerClient.cs
- backend-dotnet/RuBackend/Worker/WorkerModels.cs
- backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs
- backend-dotnet/RuBackend/Worker/FastApiWorkerClient.cs
- src/mini_gpt/worker_server.py
- src/mini_gpt/worker_runtime_handlers.py
- tests/test_worker_runtime_handlers.py
- backend-dotnet/RuBackend/Program.cs
- backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
- backend-dotnet/compatibility-route-matrix.json
- docs/aspnetcore-route-matrix.md
- scripts/aspnetcore_compatibility_harness.py
- tests/test_aspnetcore_compatibility_tools.py
- gui-winui3/Services/BackendLaunchPlan.cs
- gui-winui3/Services/BackendProcessManager.cs
- gui-winui3.Tests/BackendLaunchPlanTests.cs
- gui-winui3.Tests/BackendProcessManagerTests.cs
- build.bat
- backend-dotnet/README.md
- README.md
- docs/aspnetcore-cutover-validation.md

## Runtime interfaces

Use these exact interfaces for the runtime slice:

~~~csharp
public sealed record VoiceStartRequest(
    string RequestId,
    string Text,
    string VoiceLabel,
    double Speed,
    string VoiceStyleId);

public sealed record VoiceControlEvent(
    string RequestId,
    string State,
    long Sequence,
    int? AudioLength,
    string? ErrorCode);

public sealed record VoiceStreamEvent(
    VoiceControlEvent Control,
    ReadOnlyMemory<byte> Audio);

public sealed record VoiceStatus(
    bool Available,
    string VoiceLabel,
    string VoiceStyleId,
    double VoiceSpeed,
    bool AutoPaceEnabled);

public sealed record VtsStatus(bool Connected, string? ModelName);
public sealed record ObservationStatus(string State, string? Provider, string? Message);

public interface IWorkerClientRuntime
{
    Task<IReadOnlyList<string>> GetModelsAsync(string backend, CancellationToken cancellationToken);
    Task<JsonObject> LoadRuntimeAsync(JsonObject payload, CancellationToken cancellationToken);
    Task<VoiceStatus> GetVoiceStatusAsync(CancellationToken cancellationToken);
    Task<byte[]> SynthesizeVoiceAsync(string text, string voiceLabel, double speed, string voiceStyleId, CancellationToken cancellationToken);
    Task WarmupVoiceAsync(CancellationToken cancellationToken);
    Task ReleaseVoiceAsync(CancellationToken cancellationToken);
    Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, int sampleRate, CancellationToken cancellationToken);
    IAsyncEnumerable<VoiceStreamEvent> StreamVoiceAsync(VoiceStartRequest request, CancellationToken cancellationToken);
    Task ReportVoiceLatencyAsync(JsonObject payload, CancellationToken cancellationToken);
    Task<VtsStatus> GetVtsStatusAsync(CancellationToken cancellationToken);
    Task ConnectVtsAsync(CancellationToken cancellationToken);
    Task<JsonObject> GetVtsConfigAsync(CancellationToken cancellationToken);
    Task SaveVtsConfigAsync(JsonObject payload, CancellationToken cancellationToken);
    Task TriggerVtsActionAsync(JsonObject payload, CancellationToken cancellationToken);
    Task SetVtsSpeakingAsync(bool speaking, CancellationToken cancellationToken);
    Task<JsonObject> GetObservationConfigAsync(CancellationToken cancellationToken);
    Task<JsonObject> ReactToObservationAsync(JsonObject payload, CancellationToken cancellationToken);
    Task CancelObservationAsync(CancellationToken cancellationToken);
    Task AcknowledgeObservationAsync(JsonObject payload, CancellationToken cancellationToken);
    Task StartObservationAsync(CancellationToken cancellationToken);
    Task StopObservationAsync(CancellationToken cancellationToken);
}
~~~

The runtime slice extends the core IWorkerClient with IWorkerClientRuntime. The FastAPI and loopback adapters implement the same combined interface; public endpoints never branch on transport.

### Task 1: Freeze remaining route contracts and extend worker runtime envelopes

**Files:**
- Create: tests/fixtures/runtime-route-contract.json
- Create: backend-dotnet/RuBackend/Runtime/RuntimeModels.cs
- Modify: backend-dotnet/RuBackend/Worker/IWorkerClient.cs
- Modify: backend-dotnet/RuBackend/Worker/WorkerModels.cs
- Modify: backend-dotnet/RuBackend/Worker/WorkerMessageTypes.cs
- Modify: backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs
- Create: backend-dotnet/RuBackend.Tests/Runtime/StatusEndpointsTests.cs

- [ ] **Step 1: Write failing route/envelope tests**

Create synthetic tests for voice status/tts/warmup/release/transcribe/latency, voice stream start/cancel/playback, VTS status/connect/config/activity/action/speaking, observation preflight/config/react/cancel/ack/start/stop/status, and status/models. Assert exact JSON property names, enum strings, null handling, and private worker message types.

Run:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Runtime
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WorkerEnvelopeTests
~~~

Expected: FAIL because runtime models/message types are absent.

- [ ] **Step 2: Add runtime worker message types and models**

Add models.list, runtime.load, runtime.state, voice.start, voice.cancel, voice.playback, voice.control, voice.audio, voice.done, voice.status, voice.tts, voice.warmup, voice.release, voice.transcribe, voice.latency, vts.status, vts.connect, vts.config, vts.activity, vts.action, vts.speaking, observation.config, observation.react, observation.cancel, observation.ack, observation.start, observation.stop, and observation.status. Keep binary audio associated with request ID and sequence.

- [ ] **Step 3: Extend both worker adapters**

Add IWorkerClientRuntime methods exactly as defined above. The FastAPI adapter translates the current route contracts; the loopback adapter sends the new envelopes. Add deterministic fake implementations for tests.

- [ ] **Step 4: Run focused worker tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Worker
~~~

Expected: all existing and new worker tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add tests/fixtures/runtime-route-contract.json backend-dotnet/RuBackend/Runtime backend-dotnet/RuBackend/Worker backend-dotnet/RuBackend.Tests/Worker backend-dotnet/RuBackend.Tests/Runtime/StatusEndpointsTests.cs
git commit -m "feat: define runtime worker route contracts"
~~~

### Task 2: Connect real Python voice handlers and direct voice HTTP endpoints

**Files:**
- Modify: src/mini_gpt/worker_server.py
- Modify: src/mini_gpt/worker_runtime_handlers.py
- Modify: tests/test_worker_runtime_handlers.py
- Create: backend-dotnet/RuBackend/Runtime/VoiceEndpoints.cs
- Create: backend-dotnet/RuBackend.Tests/Runtime/VoiceEndpointsTests.cs

- [ ] **Step 1: Write failing voice tests**

Use fake worker runtime responses to test exact behavior for voice status, TTS input length, voice label/style validation, speed bounds, warmup, release, transcribe base64/sample rate validation, and latency payload finite-number validation. Verify secret and metadata redaction.

Run the focused tests. Expected: FAIL because direct voice endpoints are absent.

- [ ] **Step 2: Implement Python voice worker handlers**

Adapt the existing voice router functions and VoiceStreamCoordinator behind worker_runtime_handlers.py. Preserve saved voice selection, original style-only policy, native speed, GPU lifecycle, 90-second TTS timeout, 30-second lifecycle timeout, and Google transcription behavior. Return structured errors without text/audio in diagnostics.

- [ ] **Step 3: Implement direct VoiceEndpoints**

Map:

- GET /voice/status;
- POST /voice/latency;
- POST /voice/tts;
- POST /voice/warmup;
- POST /voice/release;
- POST /voice/transcribe.

Keep existing Pydantic-equivalent validation in C#, exact status/error shapes, 8 MiB request limit, 12,000-character text limit, 7,500,000-character audio base64 limit, 8,000..192,000 sample-rate range, 0.5..2.0 speed range, and finite latency checks.

- [ ] **Step 4: Run focused voice tests and existing Python voice tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~VoiceEndpointsTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_voice_stream.py tests/test_voice_runtime.py tests/test_voice_pacing.py tests/test_server_api.py -q
~~~

Expected: all tests pass sequentially.

- [ ] **Step 5: Commit**

~~~powershell
git add src/mini_gpt/worker_server.py src/mini_gpt/worker_runtime_handlers.py tests/test_worker_runtime_handlers.py backend-dotnet/RuBackend/Runtime/VoiceEndpoints.cs backend-dotnet/RuBackend.Tests/Runtime/VoiceEndpointsTests.cs
git commit -m "feat: move voice HTTP ownership to ASP.NET"
~~~

### Task 3: Move voice WebSocket stream ownership and confirm real audio

**Files:**
- Create: backend-dotnet/RuBackend/Runtime/VoiceStreamEndpoint.cs
- Create: backend-dotnet/RuBackend.Tests/Runtime/VoiceStreamEndpointTests.cs
- Modify: backend-dotnet/RuBackend/Program.cs
- Modify: scripts/aspnetcore_compatibility_harness.py
- Modify: tests/test_aspnetcore_compatibility_tools.py

- [ ] **Step 1: Write failing stream tests**

Test unauthorized close 4401, invalid JSON, invalid request types, start/cancel/playback flow, stream-active error, request ID matching, control sequence monotonicity, and the requirement that each audio_ready control is followed by non-empty binary audio.

Run focused tests. Expected: FAIL because direct VoiceStreamEndpoint is absent.

- [ ] **Step 2: Implement the direct stream endpoint**

Authenticate the query token exactly as the current WinUI VoiceStreamClient does. Parse start/cancel/playback messages, call IWorkerClientRuntime.StreamVoiceAsync, send control JSON before binary audio, forward cancellation on disconnect, and never create a second playback queue. Reject a missing binary pair as a worker protocol error and send one failed terminal event.

- [ ] **Step 3: Add ownership selection**

Add RuBackend:Ownership:Voice with python, shadow, and aspnet. Keep the existing relay for python. Map /voice/stream directly only for aspnet. Shadow mode uses synthetic worker output only and never duplicates real TTS.

- [ ] **Step 4: Run focused and live audio gates**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~VoiceStreamEndpointTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_aspnetcore_compatibility_tools.py tests/test_voice_stream.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/aspnetcore_compatibility_harness.py --voice-owner aspnet --require-voice-audio --isolated
~~~

Expected: tests pass; live output confirms non-empty audio after the matching control frame, child cleanup, and no leaked port.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Runtime/VoiceStreamEndpoint.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/Runtime/VoiceStreamEndpointTests.cs scripts/aspnetcore_compatibility_harness.py tests/test_aspnetcore_compatibility_tools.py
git commit -m "feat: move voice streaming ownership to ASP.NET"
~~~

### Task 4: Move VTS endpoints without duplicating VTS runtime

**Files:**
- Create: backend-dotnet/RuBackend/Runtime/VtsEndpoints.cs
- Create: backend-dotnet/RuBackend.Tests/Runtime/VtsEndpointsTests.cs
- Modify: src/mini_gpt/worker_runtime_handlers.py
- Modify: tests/test_worker_runtime_handlers.py
- Modify: backend-dotnet/RuBackend/Program.cs

- [ ] **Step 1: Write failing VTS tests**

Cover mouse-look, overlay, status, connect, config read/save, activity, action, and speaking routes. Test exact payloads, unauthorized responses, unavailable VTS errors, and that codes are passed privately without appearing in response diagnostics.

Run focused tests. Expected: FAIL because direct VTS endpoints are absent.

- [ ] **Step 2: Implement Python VTS handlers**

Adapt the existing VTS router and manager calls. Keep model-scoped catalog validation, explicit action fallback, speaking state, overlay/mouse-look persistence, and the existing VTS locks. Do not move the VTS scheduler or WebSocket state into ASP.NET.

- [ ] **Step 3: Implement direct VtsEndpoints**

Map all /vts routes through IWorkerClientRuntime with the existing JSON response shapes. Validate payload sizes and types before worker calls. Return deterministic unavailable responses instead of claiming an action happened.

- [ ] **Step 4: Run focused VTS tests and selected Python tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~VtsEndpointsTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_vts_api.py tests/test_server_api.py -q
~~~

Expected: all tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Runtime/VtsEndpoints.cs backend-dotnet/RuBackend.Tests/Runtime/VtsEndpointsTests.cs src/mini_gpt/worker_runtime_handlers.py tests/test_worker_runtime_handlers.py backend-dotnet/RuBackend/Program.cs
git commit -m "feat: move VTS HTTP ownership to ASP.NET"
~~~

### Task 5: Move observation and status/model routes

**Files:**
- Create: backend-dotnet/RuBackend/Runtime/ObservationEndpoints.cs
- Create: backend-dotnet/RuBackend/Runtime/StatusEndpoints.cs
- Create: backend-dotnet/RuBackend.Tests/Runtime/ObservationEndpointsTests.cs
- Create: backend-dotnet/RuBackend.Tests/Runtime/StatusEndpointsTests.cs
- Modify: src/mini_gpt/worker_runtime_handlers.py
- Modify: tests/test_worker_runtime_handlers.py
- Modify: backend-dotnet/RuBackend/Program.cs

- [ ] **Step 1: Write failing observation/status tests**

Test observation preflight, vision-config, react, cancel, ack, start, stop, status, plus status, models, and runtime/model selection. When no observation provider is configured, assert the exact disabled state instead of a false active claim. Test model listing errors, atomic runtime-load rollback, and redacted credential status.

Run focused tests. Expected: FAIL because direct endpoints are absent.

- [ ] **Step 2: Implement worker handlers**

Preserve disabled observation semantics, cloud reaction lifecycle, cancellation, acknowledgement, current model discovery, and backend/model load rollback through private worker calls. Do not log screen text or provider credentials.

- [ ] **Step 3: Implement direct endpoint modules**

Map all remaining HTTP routes in the route matrix. Keep existing auth/request-size middleware and exact public response shapes. POST /settings uses LoadRuntimeAsync as a transaction: load the candidate runtime, persist credentials/settings only after success, and restore the previous runtime/settings/credentials on failure. Health/readiness must prove the worker is reachable, not merely that Kestrel is listening.

- [ ] **Step 4: Run route and endpoint tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ObservationEndpointsTests
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~StatusEndpointsTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/check_aspnetcore_route_matrix.py
~~~

Expected: all tests pass and the route checker reports 38 HTTP method/path pairs and two WebSocket paths.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Runtime backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/Runtime src/mini_gpt/worker_runtime_handlers.py tests/test_worker_runtime_handlers.py backend-dotnet/compatibility-route-matrix.json docs/aspnetcore-route-matrix.md
git commit -m "feat: move remaining runtime routes to ASP.NET"
~~~

### Task 6: Remove FastAPI from the default process topology

**Files:**
- Modify: gui-winui3/Services/BackendLaunchPlan.cs
- Modify: gui-winui3/Services/BackendProcessManager.cs
- Modify: gui-winui3.Tests/BackendLaunchPlanTests.cs
- Modify: gui-winui3.Tests/BackendProcessManagerTests.cs
- Modify: build.bat
- Modify: backend-dotnet/RuBackend/Program.cs
- Modify: scripts/aspnetcore_compatibility_harness.py
- Modify: backend-dotnet/compatibility-route-matrix.json

- [ ] **Step 1: Write failing topology tests**

Assert that final aspnet mode starts exactly RuBackend plus Python worker, assigns both to one Job Object, waits for worker-backed readiness, and leaves no FastAPI child or public upstream port. Assert that explicit fastapi mode still starts the old rollback topology.

Run:

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~BackendLaunchPlanTests
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~BackendProcessManagerTests
~~~

Expected: FAIL until the final topology is implemented.

- [ ] **Step 2: Implement final process topology**

In aspnet mode, start python -m mini_gpt.worker_server on the private worker port, then published RuBackend on public 8000 with the worker URL and token. Keep the same Job Object, fail-closed readiness, cleanup, and explicit FastAPI rollback. Do not add a silent port fallback.

- [ ] **Step 3: Remove public forwarding only after route matrix is direct**

Delete or disable the compatibility catch-all only when every route is direct ASP.NET or a worker adapter and tests prove no route depends on FastAPI. Keep the compatibility code available in the explicit fastapi rollback build path until the final release tag.

- [ ] **Step 4: Update build and publish checks**

Ensure build.bat publishes the self-contained RuBackend and RuApp, includes the worker launch/runtime files, and does not claim FastAPI is part of the default process. Run the existing build and PRI generation.

- [ ] **Step 5: Run topology tests and live smoke**

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Backend
cmd.exe /d /c build.bat
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/aspnetcore_compatibility_harness.py --worker-mode --require-voice-audio --isolated
~~~

Expected: full build succeeds, public 8000 and private worker port are ready, all route/WS/audio checks pass, and both child processes terminate with no listeners remaining.

- [ ] **Step 6: Commit final topology**

~~~powershell
git add gui-winui3/Services/BackendLaunchPlan.cs gui-winui3/Services/BackendProcessManager.cs gui-winui3.Tests/BackendLaunchPlanTests.cs gui-winui3.Tests/BackendProcessManagerTests.cs build.bat backend-dotnet/RuBackend/Program.cs scripts/aspnetcore_compatibility_harness.py backend-dotnet/compatibility-route-matrix.json
git commit -m "feat: remove FastAPI from the default process topology"
~~~

### Task 7: Run final route, audio, rollback, and documentation acceptance

**Files:**
- Modify: docs/aspnetcore-cutover-validation.md
- Modify: backend-dotnet/README.md
- Modify: README.md
- Modify: docs/aspnetcore-route-matrix.md

- [ ] **Step 1: Run all sequential gates**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py tests/test_worker_protocol.py tests/test_worker_server.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/check_aspnetcore_route_matrix.py
cmd.exe /d /c build.bat
~~~

Expected: every command exits zero.

- [ ] **Step 2: Run the final isolated live harness**

Verify all safe route probes, chat ordering, voice invalid-control behavior, confirmed non-empty voice audio, VTS unavailable honesty, observation disabled state, worker/edge readiness, child cleanup, no leaked port, and redacted logs. Save only machine-readable evidence under ignored artifacts/aspnetcore/.

- [ ] **Step 3: Run explicit FastAPI rollback**

Start the published app in fastapi mode, verify the original Python route matrix and chat/voice paths, stop it, then start final aspnet worker mode again. Verify the same data files remain readable and no process survives shutdown.

- [ ] **Step 4: Record final evidence and current status**

Update route classifications, topology, worker protocol version, audio confirmation, full test counts, build output, rollback result, and direct-versus-worker latency. State explicitly that ASP.NET removes the proxy once FastAPI is gone but does not make Python inference intrinsically faster.

- [ ] **Step 5: Commit final acceptance documents**

~~~powershell
git add docs/aspnetcore-cutover-validation.md backend-dotnet/README.md README.md docs/aspnetcore-route-matrix.md
git commit -m "docs: record final FastAPI decommission acceptance"
~~~

## Self-review checklist

- [ ] Every route-matrix entry, runtime worker method, voice binary gate, VTS privacy rule, observation disabled state, process cleanup, rollback, build, and documentation requirement maps to a task above.
- [ ] Scan this plan for placeholder markers and vague steps; none may remain.
- [ ] Confirm RuntimeModels, IWorkerClientRuntime, VoiceStreamEvent, direct endpoint names, worker message types, and process mode names are identical in every task.
- [ ] Run git diff --check after every plan edit.
