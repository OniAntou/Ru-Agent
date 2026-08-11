# Ru Python Worker Protocol Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the FastAPI-only internal transport with a versioned loopback worker protocol that ASP.NET can use for model, memory-learning, voice, VTS, and observation runtime calls.

**Architecture:** Define one language-neutral envelope contract and implement it in C# and Python. ASP.NET owns the public request and event contracts through IWorkerClient; a FastApiWorkerClient preserves rollback while LoopbackWorkerClient connects to a token-protected Python WebSocket worker. Streaming chat and audio use request IDs and monotonically increasing sequence numbers.

**Tech Stack:** ASP.NET Core WebSockets, C# System.Net.WebSockets, Python asyncio, Python websockets, JSON, xUnit, pytest, and the existing process-manager/live-harness infrastructure.

## Execution Status and Amendments (2026-08-11)

- Approved for inline execution on `main`; no sub-agents.
- `Runtime/IRuntimeWorkerClient`, `FastApiRuntimeWorkerClient`, the STT endpoint,
  and the VTS endpoint seam already exist. The versioned `IWorkerClient` becomes
  the replacement interface; adapters must be migrated without creating a
  second competing runtime abstraction.
- The per-run worker token is passed only through inherited environment
  variables, never a command-line argument or log path.
- Correct Vietnamese fixture text is UTF-8 (`Em nghe đây.`); mojibake fixtures
  are invalid and must fail parity tests.
- FastAPI compatibility exists only until the final decommission gate. After
  that gate its adapter, dependencies, process mode, and source are deleted.
- Protected dirty Python/WinUI changes are behavior to preserve and port, not
  files to reset or stage wholesale.

## Global Constraints

- The worker listens only on 127.0.0.1 and uses a per-run token handshake.
- Every control envelope contains v, id, type, and payload; streamed events contain seq when ordering matters.
- Chat reset/chunk/done/error ordering and voice control/binary pairing must remain intact.
- Cancellation propagates from the public ASP.NET CancellationToken to Python and closes stale requests.
- Unknown protocol versions, malformed envelopes, missing sequence numbers, and worker health failures fail closed.
- Worker diagnostics are redacted: no token, API key, prompt, response, audio bytes, or private VTS metadata.
- The initial FastApiWorkerClient remains available for rollback until the final decommission gate.
- The worker protocol does not own SQLite or deterministic business stores; it receives explicit request payloads.
- Run Python, .NET, GUI, and live process checks sequentially.
- Review README.md, backend-dotnet/README.md, route matrix, validation evidence, and the migration design after the slice.

---

## File Map

Create:

- backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs — common envelope and JSON serialization.
- backend-dotnet/RuBackend/Worker/WorkerMessageTypes.cs — stable message type constants.
- backend-dotnet/RuBackend/Worker/WorkerClientOptions.cs — loopback URI, token, and protocol version.
- backend-dotnet/RuBackend/Worker/WorkerModels.cs — chat, memory-extraction, voice, VTS, and worker event records.
- backend-dotnet/RuBackend/Worker/IWorkerClient.cs — business-facing worker interface.
- backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs — WebSocket client and frame ordering.
- backend-dotnet/RuBackend/Worker/FastApiWorkerClient.cs — rollback adapter over existing FastAPI routes.
- backend-dotnet/RuBackend/Worker/WorkerConnection.cs — handshake, send, receive, and cancellation lifecycle.
- backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs
- backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs
- backend-dotnet/RuBackend.Tests/Worker/FastApiWorkerClientTests.cs
- src/mini_gpt/worker_protocol.py — Python envelope validation and framing helpers.
- src/mini_gpt/worker_server.py — Python loopback WebSocket server and handler registry.
- tests/test_worker_protocol.py — Python schema/framing tests.
- tests/test_worker_server.py — Python fake-handler server tests.
- scripts/worker_protocol_harness.py — isolated cross-language handshake/streaming harness.

Modify:

- backend-dotnet/RuBackend/RuBackend.csproj — no package beyond the framework unless the existing SDK lacks required WebSocket APIs.
- backend-dotnet/RuBackend/Program.cs — register WorkerClient and health endpoint only when worker mode is selected.
- gui-winui3/Services/BackendLaunchPlan.cs — add worker-process command/environment specifications.
- gui-winui3/Services/BackendProcessManager.cs — own the worker child in the same Job Object.
- scripts/aspnetcore_compatibility_harness.py — add worker protocol mode without changing default rollback mode.
- pyproject.toml — pin the already-used websockets package if the worker server cannot import it from the current server extra.
- backend-dotnet/README.md
- README.md
- docs/aspnetcore-cutover-validation.md

## Protocol contract

Use these exact JSON control shapes:

~~~json
{"v":1,"id":"conn-1","type":"hello","payload":{"token":"opaque-secret","capabilities":["chat","memory.extract","voice","vts","observation"]}}
{"v":1,"id":"conn-1","type":"hello.ok","seq":0,"payload":{"version":1,"capabilities":["chat","memory.extract"]}}
{"v":1,"id":"req-1","type":"chat.start","payload":{"prompt":"synthetic test","session_id":"s1","turns":[],"memory_context":"","vts_instruction":"","settings":{}}}
{"v":1,"id":"req-1","type":"chat.reset","seq":0,"payload":{}}
{"v":1,"id":"req-1","type":"chat.chunk","seq":1,"payload":{"text":"Em nghe đây."}}
{"v":1,"id":"req-1","type":"chat.done","seq":2,"payload":{"raw_reply":"Em nghe đây."}}
{"v":1,"id":"req-1","type":"chat.cancel","payload":{}}
{"v":1,"id":"req-2","type":"memory.extract","payload":{"prompt":"...","response":"..."}}
{"v":1,"id":"req-2","type":"memory.facts","seq":0,"payload":{"facts":[]}}
{"v":1,"id":"req-3","type":"voice.start","payload":{"request_id":"voice-1","text":"...","voice_label":"Ru (OmniVoice)","speed":1.0,"voice_style_id":"original"}}
{"v":1,"id":"req-3","type":"voice.control","seq":0,"payload":{"request_id":"voice-1","state":"audio_ready","byte_length":1234}}
{"v":1,"id":"req-3","type":"voice.done","seq":1,"payload":{"request_id":"voice-1"}}
{"v":1,"id":"req-4","type":"vts.catalog","payload":{}}
{"v":1,"id":"req-5","type":"vts.action","payload":{"codes":[]}}
{"v":1,"id":"req-health","type":"worker.health","payload":{}}
{"v":1,"id":"req-health","type":"worker.health.ok","seq":0,"payload":{"ready":true,"version":1}}
{"v":1,"id":"req-6","type":"error","seq":0,"payload":{"code":"worker_unavailable","retryable":true}}
~~~

A voice.control frame announcing audio must be followed immediately by one non-empty binary frame for the same request ID and sequence. The client rejects an audio_ready control without a matching binary frame.

Use these C# types:

~~~csharp
public sealed record WorkerEnvelope(
    int V,
    string Id,
    string Type,
    long? Seq,
    JsonObject Payload);

public sealed record WorkerChatRequest(
    string RequestId,
    string Prompt,
    string SessionId,
    IReadOnlyList<WorkerTurn> Turns,
    string MemoryContext,
    string VtsInstruction,
    JsonObject Settings);

public sealed record WorkerTurn(string Prompt, string Response);

public sealed record WorkerEvent(
    string RequestId,
    string Type,
    long? Sequence,
    JsonObject Payload,
    ReadOnlyMemory<byte> Binary);

public sealed record WorkerHello(
    int Version,
    IReadOnlySet<string> Capabilities);

public sealed record WorkerHealth(
    bool Ready,
    int Version,
    IReadOnlySet<string> Capabilities);

public sealed record WorkerFact(
    string Kind,
    string Content,
    string? Scope,
    double Confidence,
    double Importance);

public interface IWorkerClient : IAsyncDisposable
{
    Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<WorkerEvent> GenerateChatAsync(
        WorkerChatRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkerFact>> ExtractFactsAsync(
        string requestId,
        string prompt,
        string response,
        CancellationToken cancellationToken);
    Task CancelAsync(string requestId, CancellationToken cancellationToken);
    Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken);
}
~~~

VTS methods are added by the chat plan before direct chat ownership; voice and observation methods are added by the runtime-slice plan after chat is proven. The runtime slice extends IWorkerClient with IWorkerClientRuntime; until then core modules depend only on the methods above. Do not make business modules depend on concrete WebSocket classes.

### Task 1: Add protocol fixtures, constants, and envelope validation

**Files:**
- Create: backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs
- Create: backend-dotnet/RuBackend/Worker/WorkerMessageTypes.cs
- Create: backend-dotnet/RuBackend/Worker/WorkerModels.cs
- Create: backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs
- Create: tests/test_worker_protocol.py

- [x] **Step 1: Write failing C# and Python schema tests**

Test valid hello/chat/voice/error envelopes, required fields, unsupported versions, missing IDs, invalid sequence values, non-object payloads, and JSON round-trip. Test that tokens are not included in any string returned by redaction helpers.

Run sequentially:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WorkerEnvelopeTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_worker_protocol.py -q
~~~

Expected: FAIL because the protocol modules do not exist.

- [x] **Step 2: Implement shared constants and models**

Create message-type constants for hello, hello.ok, chat.start, chat.reset, chat.chunk, chat.done, chat.cancel, memory.extract, memory.facts, voice.start, voice.control, voice.done, vts.catalog, vts.action, and error. Reject any envelope whose V is not 1, Id is blank or longer than 128 characters, Type is unknown, Payload is not an object, or Seq is negative.

Use System.Text.Json with camelCase properties and ignore no required field. Python validation must produce the same normalized dictionary keys and error codes.

- [x] **Step 3: Run focused tests**

Run the same commands. Expected: all C# and Python protocol tests pass.

- [x] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Worker backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs tests/test_worker_protocol.py
git commit -m "feat: define versioned Python worker protocol"
~~~

### Task 2: Implement the Python worker server with deterministic fake handlers

**Files:**
- Create: src/mini_gpt/worker_server.py
- Modify: src/mini_gpt/worker_protocol.py
- Create: tests/test_worker_server.py

- [x] **Step 1: Write failing fake-handler tests**

Start the server on an ephemeral loopback port with handlers that emit a deterministic chat reset/chunk/done sequence and a memory.facts response. Test hello success, bad token close, unknown version error, cancel event, and clean shutdown.

Run:

~~~powershell
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_worker_protocol.py tests/test_worker_server.py -q
~~~

Expected: FAIL because the server and handler registry are absent.

- [x] **Step 2: Implement Python framing and server**

Use asyncio and websockets. Authenticate the first message before accepting work. Keep a per-connection dictionary of request tasks keyed by request ID. Parse text control frames with worker_protocol.py. Send control envelopes as JSON text. Reply to worker.health with worker.health.ok. Send voice audio only after the matching voice.control JSON frame and validate non-empty bytes before sending. On chat.cancel, cancel the matching task and send a terminal error or cancelled event exactly once. Close all child request tasks on connection shutdown.

Expose:

~~~python
async def serve_worker(
    host: str,
    port: int,
    token: str,
    handlers: WorkerHandlers,
) -> None: ...
~~~

where WorkerHandlers has async callables for chat, memory extraction, voice, VTS, and observation.

- [x] **Step 3: Run Python tests**

Run the focused command. Expected: all server tests pass with no secret in captured logs.

- [x] **Step 4: Commit**

~~~powershell
git add src/mini_gpt/worker_protocol.py src/mini_gpt/worker_server.py tests/test_worker_protocol.py tests/test_worker_server.py
git commit -m "feat: add loopback Python worker server"
~~~

### Task 3: Implement C# connection lifecycle and LoopbackWorkerClient

**Files:**
- Create: backend-dotnet/RuBackend/Worker/WorkerClientOptions.cs
- Create: backend-dotnet/RuBackend/Worker/WorkerConnection.cs
- Create: backend-dotnet/RuBackend/Worker/IWorkerClient.cs
- Create: backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs
- Create: backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs

- [x] **Step 1: Write failing client tests**

Use a local fake WebSocket server or the test server from Task 2 to test handshake, chat reset/chunk/done ordering, request cancellation, worker unavailable mapping, malformed envelope rejection, and voice.control plus binary pairing.

~~~csharp
[Fact]
public async Task GenerateChat_rejects_audio_ready_without_matching_binary()
{
    await fakeServer.SendControlAsync(VoiceControlAudioReady);
    await fakeServer.CloseWithoutBinaryAsync();

    await Assert.ThrowsAsync<WorkerProtocolException>(
        () => client.GenerateChatAsync(request, CancellationToken.None).ToListAsync());
}
~~~

Run focused tests. Expected: FAIL because the client classes do not exist.

- [x] **Step 2: Implement WorkerConnection**

WorkerConnection opens a ClientWebSocket to the configured loopback URI, sends the hello envelope with the opaque token, validates hello.ok version 1, and exposes a single serialized send path. It reads text and binary frames, associates binary bytes with the preceding audio control event, enforces monotonic sequence per request, and maps cancellation to chat.cancel/voice.cancel plus socket close when the request is gone.

Do not log envelope payloads. Log only message type, request ID hash, sequence, and error code.

- [x] **Step 3: Implement LoopbackWorkerClient**

Implement the exact IWorkerClient methods. GenerateChatAsync sends chat.start and yields WorkerEvent values until done/error. ExtractFactsAsync sends memory.extract and accepts only memory.facts or error. GetHealthAsync performs hello plus a worker health request and returns protocol version/capabilities. CancelAsync sends the corresponding cancel envelope.

- [x] **Step 4: Run focused client tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~LoopbackWorkerClientTests
~~~

Expected: all handshake, order, cancellation, and binary-pairing tests pass.

- [x] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Worker backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs
git commit -m "feat: add ASP.NET loopback worker client"
~~~

### Task 4: Implement FastApiWorkerClient as the rollback adapter

**Files:**
- Create: backend-dotnet/RuBackend/Worker/FastApiWorkerClient.cs
- Create: backend-dotnet/RuBackend.Tests/Worker/FastApiWorkerClientTests.cs

- [ ] **Step 1: Write failing adapter contract tests**

Use fake HTTP/WebSocket transports to assert that FastApiWorkerClient maps current /chat and health behavior into the same WorkerEvent types as LoopbackWorkerClient. In FastAPI ownership mode, memory extraction remains Python-owned; ExtractFactsAsync must return a deterministic unsupported error rather than enabling a second memory writer. Test upstream 503, malformed JSON, and cancellation.

Run focused tests. Expected: FAIL because the adapter is absent.

- [ ] **Step 2: Implement the adapter**

Use the existing private FastAPI worker URL and token. Preserve public chat event ordering and the current query-token WebSocket auth contract. Do not duplicate business logic; the adapter only translates transport frames into the worker interface. Redact all diagnostic output.

- [ ] **Step 3: Run adapter and existing relay tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~FastApiWorkerClientTests
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WebSocketRelayTests
~~~

Expected: all focused tests pass and the existing relay remains unchanged.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Worker/FastApiWorkerClient.cs backend-dotnet/RuBackend.Tests/Worker/FastApiWorkerClientTests.cs
git commit -m "feat: keep FastAPI worker rollback adapter"
~~~

### Task 5: Connect real Python runtime handlers

**Files:**
- Modify: src/mini_gpt/worker_server.py
- Create: src/mini_gpt/worker_runtime_handlers.py
- Create: tests/test_worker_runtime_handlers.py
- Modify: src/mini_gpt/backend_core.py
- Modify: src/mini_gpt/_server_shared.py

- [ ] **Step 1: Write failing handler contract tests**

Use fakes for ModelRunner, StructuredMemoryStore, voice transport, and VTS manager. Assert that chat receives explicit prompt/history/memory/settings, memory extraction returns at most five facts, VTS metadata is kept private, and worker errors contain only stable error codes.

Run:

~~~powershell
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_worker_runtime_handlers.py -q
~~~

Expected: FAIL because the handler module is absent.

- [ ] **Step 2: Implement handlers without moving ownership back into Python**

worker_runtime_handlers.py adapts the existing core/runtime functions to WorkerHandlers. It must not open or write memory.db after the ASP.NET memory ownership flag is enabled. It may read model/runtime configuration sent in the request. It must retain the existing GPU lifecycle and voice service boundaries.

- [ ] **Step 3: Run Python runtime handler tests and selected server suite**

~~~powershell
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_worker_runtime_handlers.py tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
~~~

Expected: all tests pass sequentially; existing FastAPI behavior remains available for rollback.

- [ ] **Step 4: Commit**

~~~powershell
git add src/mini_gpt/worker_server.py src/mini_gpt/worker_runtime_handlers.py src/mini_gpt/backend_core.py src/mini_gpt/_server_shared.py tests/test_worker_runtime_handlers.py
git commit -m "feat: adapt Python runtime to worker protocol"
~~~

### Task 6: Add process-manager worker ownership and isolated harness

**Files:**
- Modify: gui-winui3/Services/BackendLaunchPlan.cs
- Modify: gui-winui3/Services/BackendProcessManager.cs
- Modify: gui-winui3.Tests/BackendLaunchPlanTests.cs
- Modify: gui-winui3.Tests/BackendProcessManagerTests.cs
- Create: scripts/worker_protocol_harness.py
- Modify: scripts/aspnetcore_compatibility_harness.py
- Modify: backend-dotnet/RuBackend/Program.cs

- [ ] **Step 1: Add failing launch/process tests**

Test that worker mode creates one shared Job Object, uses a private configurable worker port, passes the same token to ASP.NET and Python, waits for worker health before public readiness, and terminates both children on disposal. Test that fastapi rollback still starts the existing worker topology unchanged.

Run:

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~BackendLaunchPlan
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~BackendProcessManager
~~~

Expected: FAIL for the new worker mode.

- [ ] **Step 2: Implement launch specs and readiness**

Add a distinct worker role without changing the existing FastApi/AspNetCore parser contract during this intermediate slice. The worker command is `python -m mini_gpt.worker_server --host 127.0.0.1 --port <worker-port>`; the opaque token is supplied through `RU_WORKER_TOKEN` in the child environment. RuBackend receives `RuBackend:WorkerUrl` and the same token through its environment. Fail closed if the worker executable, health handshake, Job Object assignment, or public readiness fails.

- [ ] **Step 3: Run GUI tests and Python harness tests**

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Backend
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_aspnetcore_compatibility_tools.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/worker_protocol_harness.py --isolated --samples 1
~~~

Expected: all process tests pass, the isolated worker harness exits zero, and no listener remains after cleanup.

- [ ] **Step 4: Commit**

~~~powershell
git add gui-winui3/Services/BackendLaunchPlan.cs gui-winui3/Services/BackendProcessManager.cs gui-winui3.Tests scripts/worker_protocol_harness.py scripts/aspnetcore_compatibility_harness.py backend-dotnet/RuBackend/Program.cs
git commit -m "feat: own the Python worker process from WinUI"
~~~

### Task 7: Record worker protocol acceptance

**Files:**
- Modify: backend-dotnet/README.md
- Modify: README.md
- Modify: docs/aspnetcore-cutover-validation.md

- [ ] **Step 1: Run the full worker contract gate sequentially**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Worker
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Backend
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_worker_protocol.py tests/test_worker_server.py tests/test_worker_runtime_handlers.py tests/test_aspnetcore_compatibility_tools.py -q
~~~

Expected: all focused tests pass.

- [ ] **Step 2: Run live chat/voice protocol evidence**

Run the isolated harness with synthetic chat and real configured voice audio. Record handshake, event ordering, cancellation, binary audio confirmation, child termination, and port cleanup. Store only redacted counts, hashes, error codes, and timing.

- [ ] **Step 3: Update documentation**

Document worker port/configuration, FastAPI rollback, protocol version, process ownership, and the fact that model/TTS latency remains Python runtime latency.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/README.md README.md docs/aspnetcore-cutover-validation.md
git commit -m "docs: record Python worker protocol acceptance"
~~~

## Self-review checklist

- [ ] Every protocol envelope, version, token, sequence, cancellation, binary-pairing, rollback, and process-ownership requirement maps to a task above.
- [ ] Scan this plan for placeholder markers and vague steps; none may remain.
- [ ] Confirm WorkerEnvelope, WorkerEvent, WorkerChatRequest, IWorkerClient, WorkerHandlers, and WorkerClientOptions signatures are identical in every task.
- [ ] Run git diff --check after every plan edit.
