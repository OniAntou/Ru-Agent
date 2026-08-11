# Ru ASP.NET Core Chat Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Move public /chat orchestration from the FastAPI route into ASP.NET Core while preserving session creation, memory context, streaming events, privacy metadata, VTS actions, cancellation, and history behavior.

**Architecture:** ConversationCoordinator owns turn ordering and concurrency, while SessionStore, MemoryService, IWorkerClient, MetadataPolicy, and ActivityLogStore remain replaceable modules. The public WebSocket endpoint translates validated WinUI frames into coordinator requests; a feature flag keeps the existing FastAPI relay for rollback.

**Tech Stack:** ASP.NET Core WebSockets, C#, System.Text.Json, Channels, xUnit, WebApplicationFactory, the worker protocol plan, and the existing WinUI ChatWebSocketClient contract.

## Execution Status (2026-08-11)

- Approved for inline execution on `main`; no sub-agents.
- Deterministic stores and the versioned worker prerequisite are implemented.
  Task 1 contract fixtures are complete; direct chat ownership is not enabled
  until Tasks 2-6 pass.
- Preserve the protected reconnect-backoff edit in
  `gui-winui3/Services/ChatWebSocketClient.cs`; direct chat ownership must pass
  the existing client tests with that behavior intact.
- The FastAPI relay is an intermediate comparison path only. It is removed by
  the final decommission plan after direct chat acceptance.
- Loopback chat now has typed private VTS catalog/action calls. The FastAPI
  adapter returns no catalog because the legacy host has no public catalog
  route; rollback chat continues to execute VTS internally and its action
  adapter remains available for typed codes.

## Global Constraints

- Preserve the public /chat query-token authentication and event types session, reset, chunk, done, and error.
- Preserve the current single-turn lock, memory lock behavior, session creation, 16,000-character prompt limit, 256 KiB WebSocket message limit, and 8 context turns.
- Preserve event ordering and never duplicate user/assistant session messages or memory writes.
- Remove VTS and voice-pacing markers before visible chat, TTS input, history, memory, reviewed examples, and diagnostics.
- VTS catalog/action calls remain private worker operations and never appear in public chat payloads.
- A client disconnect cancels the worker request and releases all locks.
- Worker failures produce one terminal error event and never leave a generation request waiting indefinitely.
- The Python FastAPI relay remains the rollback implementation until the direct route passes live chat and metadata gates.
- Run .NET, WinUI, and Python heavy checks sequentially.
- Review README.md, backend-dotnet/README.md, route matrix, validation evidence, and the migration design after the slice.

---

## File Map

Create:

- backend-dotnet/RuBackend/Conversation/ChatRequest.cs — validated public request.
- backend-dotnet/RuBackend/Conversation/ChatEvent.cs — public event DTOs.
- backend-dotnet/RuBackend/Conversation/ConversationTurnGate.cs — single-turn and memory coordination.
- backend-dotnet/RuBackend/Conversation/MetadataPolicy.cs — private marker parsing and validation.
- backend-dotnet/RuBackend/Conversation/StreamSanitizer.cs — safe visible streaming text.
- backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs — business turn orchestration.
- backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs — public WebSocket transport.
- backend-dotnet/RuBackend.Tests/Conversation/ChatContractFixtureTests.cs
- backend-dotnet/RuBackend.Tests/Conversation/MetadataPolicyTests.cs
- backend-dotnet/RuBackend.Tests/Conversation/StreamSanitizerTests.cs
- backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs
- backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs
- tests/fixtures/chat-orchestration.json — synthetic input and event expectations.

Modify:

- backend-dotnet/RuBackend/Program.cs — map direct /chat only when ownership is aspnet.
- backend-dotnet/RuBackend/Worker/IWorkerClient.cs — add private VTS catalog/action methods needed by chat.
- backend-dotnet/RuBackend/Worker/WorkerModels.cs — add VTS catalog/action types.
- backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs — cover private VTS calls.
- backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs — inject fake conversation modules.
- gui-winui3.Tests — add no-contract-change smoke coverage only if existing client tests do not cover session/chunk/done/error handling.
- scripts/aspnetcore_compatibility_harness.py — exercise direct and rollback chat modes.
- backend-dotnet/compatibility-route-matrix.json
- docs/aspnetcore-route-matrix.md
- docs/aspnetcore-cutover-validation.md

## Interfaces and invariants

Use these exact types:

~~~csharp
public sealed record ChatRequest(
    string Prompt,
    string? SessionId);

public sealed record ChatEvent(
    string Type,
    string? Content = null,
    string? SessionId = null,
    string? ErrorCode = null);

public sealed record VtsCatalog(IReadOnlySet<string> HotkeyCodes, IReadOnlySet<string> ExpressionCodes);

public interface IConversationCoordinator
{
    IAsyncEnumerable<ChatEvent> HandleAsync(
        ChatRequest request,
        CancellationToken cancellationToken);
}

public interface IMetadataPolicy
{
    (string VisibleText, IReadOnlyList<string> VtsCodes, string? Emotion) SplitReply(
        string rawReply,
        VtsCatalog? catalog);
    string StripVoiceMarkers(string text);
}

public interface IStreamSanitizer
{
    string Append(string raw, string delta);
    string Visible(string raw);
}
~~~

The coordinator consumes these existing seams:

~~~csharp
IMemoryService.RetrieveAsync(string query, CancellationToken cancellationToken)
IMemoryService.RememberTurnAsync(string prompt, string response, CancellationToken cancellationToken)
IMemoryService.EnqueueLearning(string prompt, string response)
ISessionStore.CreateAsync(CancellationToken cancellationToken)
ISessionStore.GetAsync(string sessionId, CancellationToken cancellationToken)
ISessionStore.AppendAsync(string sessionId, IReadOnlyList<SessionMessage> messages, CancellationToken cancellationToken)
ISessionStore.GetContextTurnsAsync(string sessionId, int maxTurns, CancellationToken cancellationToken)
IActivityLogStore.AppendAsync(ActivityEvent activity, CancellationToken cancellationToken)
IWorkerClient.GenerateChatAsync(WorkerChatRequest request, CancellationToken cancellationToken)
IWorkerClient.GetVtsCatalogAsync(string requestId, CancellationToken cancellationToken)
IWorkerClient.TriggerVtsActionsAsync(string requestId, IReadOnlyList<string> codes, CancellationToken cancellationToken)
~~~

### Task 1: Freeze public chat and privacy fixtures

**Files:**
- Create: tests/fixtures/chat-orchestration.json
- Create: backend-dotnet/RuBackend/Conversation/ChatRequest.cs
- Create: backend-dotnet/RuBackend/Conversation/ChatEvent.cs
- Create: backend-dotnet/RuBackend.Tests/Conversation/ChatContractFixtureTests.cs

- [x] **Step 1: Write the failing fixture test**

Use synthetic prompts and worker events to assert:

- blank prompt is rejected;
- prompt over 16,000 characters is rejected;
- invalid session IDs cause a new session event;
- normal output is session, reset, chunk, done;
- a malformed worker result produces exactly one error event;
- VTS/voice markers are absent from visible content.

Run:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ChatContractFixtureTests
~~~

Expected: FAIL because the chat contracts and fixture loader are absent.

- [x] **Step 2: Implement request/event records and fixture loader**

ChatRequest stores raw input before validation. ChatEvent serializes camelCase fields and omits null properties so the existing WinUI client sees the same JSON shape. Use JsonStringEnumConverter only for internal types; public Type remains a string.

- [x] **Step 3: Run focused tests**

Run the same command. Expected: PASS with the fixture contract asserted.

- [ ] **Step 4: Commit**

~~~powershell
git add tests/fixtures/chat-orchestration.json backend-dotnet/RuBackend/Conversation/ChatRequest.cs backend-dotnet/RuBackend/Conversation/ChatEvent.cs backend-dotnet/RuBackend.Tests/Conversation/ChatContractFixtureTests.cs
git commit -m "test: freeze ASP.NET chat orchestration contract"
~~~

### Task 2: Port metadata policy and streamed visible-text sanitization

**Files:**
- Create: backend-dotnet/RuBackend/Conversation/MetadataPolicy.cs
- Create: backend-dotnet/RuBackend/Conversation/StreamSanitizer.cs
- Create: backend-dotnet/RuBackend.Tests/Conversation/MetadataPolicyTests.cs
- Create: backend-dotnet/RuBackend.Tests/Conversation/StreamSanitizerTests.cs

- [x] **Step 1: Write failing policy tests**

Cover terminal VTS suffix parsing with one, multiple, unknown, and invalid codes; expression codes validated against VtsCatalog; emotion extraction; VOICE_PACE marker removal; VTS marker removal; markers split across streaming deltas; safe visible-prefix behavior; and an empty visible response becoming the controlled error text used by the current route.

Run the focused tests. Expected: FAIL because policy and sanitizer are absent.

- [x] **Step 2: Implement MetadataPolicy**

Port split_reply_metadata and _strip_voice_markers behavior from the Python runtime. Accept only codes present in the supplied catalog. Return visible text, validated codes, and private emotion. Never include raw marker contents in an exception or log.

- [x] **Step 3: Implement StreamSanitizer**

Keep raw and visible strings separately. Append each raw delta, then expose only the longest safe visible prefix. Hold a suffix that could still be the beginning of [[VTS: or [[VOICE_PACE: until the worker sends enough text to decide. Use NFC normalization and do not emit control markers.

- [x] **Step 4: Run focused and Python parity tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MetadataPolicyTests
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~StreamSanitizerTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py tests/test_vts_api.py -q
~~~

Expected: all focused tests pass and existing Python marker behavior stays green.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Conversation/MetadataPolicy.cs backend-dotnet/RuBackend/Conversation/StreamSanitizer.cs backend-dotnet/RuBackend.Tests/Conversation/MetadataPolicyTests.cs backend-dotnet/RuBackend.Tests/Conversation/StreamSanitizerTests.cs
git commit -m "feat: preserve private chat metadata policy"
~~~

### Task 3: Add private VTS catalog/action methods to the worker seam

**Files:**
- Modify: backend-dotnet/RuBackend/Worker/IWorkerClient.cs
- Modify: backend-dotnet/RuBackend/Worker/WorkerModels.cs
- Modify: backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs
- Modify: backend-dotnet/RuBackend/Worker/FastApiWorkerClient.cs
- Modify: backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs
- Modify: backend-dotnet/RuBackend.Tests/Worker/FastApiWorkerClientTests.cs

- [x] **Step 1: Write failing worker tests**

Test VTS catalog request/response, action request with validated codes, unavailable VTS, and malformed private responses. Assert that no catalog code or action payload is returned by public chat events.

Run the worker-focused tests. Expected: FAIL because the methods and message types are absent.

- [x] **Step 2: Add the exact worker methods**

Add:

~~~csharp
Task<VtsCatalog?> GetVtsCatalogAsync(string requestId, CancellationToken cancellationToken);
Task TriggerVtsActionsAsync(string requestId, IReadOnlyList<string> codes, CancellationToken cancellationToken);
~~~

Add worker message types vts.catalog and vts.action. The FastAPI adapter maps the existing private VTS calls; the loopback adapter maps the worker protocol. Both return only typed private values.

- [x] **Step 3: Run worker tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Worker
~~~

Expected: all worker tests pass with public event isolation assertions.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Worker backend-dotnet/RuBackend.Tests/Worker
git commit -m "feat: expose private VTS operations to chat coordinator"
~~~

### Task 4: Implement ConversationCoordinator ordering and locks

**Files:**
- Create: backend-dotnet/RuBackend/Conversation/ConversationTurnGate.cs
- Create: backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs
- Create: backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs

- [x] **Step 1: Write failing coordinator tests**

Use fake memory, session, activity, and worker modules. Assert the exact order:

1. create/load session;
2. acquire the turn gate;
3. wait for memory gate;
4. load up to eight context turns;
5. retrieve memory context;
6. request private VTS catalog;
7. send chat.start to the worker;
8. forward sanitized reset/chunk events;
9. split private metadata;
10. remember the cleaned turn;
11. append the assistant once;
12. trigger validated VTS actions;
13. emit done;
14. enqueue self-learning and activity.

Also test busy turn, memory timeout, worker error, cancellation, and cleanup after exceptions.

Run focused tests. Expected: FAIL because the coordinator is absent.

- [x] **Step 2: Implement ConversationTurnGate**

Use one SemaphoreSlim for generation and one for memory/self-learning. A second generation emits the existing busy error event without touching the session. Cancellation releases both semaphores in finally blocks. The memory gate waits at most 120 seconds for the current self-learning operation, matching the Python contract.

- [x] **Step 3: Implement ConversationCoordinator**

On a missing/invalid session ID, call SessionStore.CreateAsync and emit one session event before generation. Append the user message once before model generation. Pass explicit prompt, session ID, turns, rendered memory context, VTS instruction, and normalized settings to WorkerChatRequest.

Feed worker chat events through StreamSanitizer. After done, use MetadataPolicy, write memory, append the assistant, trigger VTS, emit done, enqueue learning, and append activity. On failure after the user append, append the same controlled assistant error text before emitting terminal error. Never include raw prompt/reply in diagnostics.

- [x] **Step 4: Run coordinator tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ConversationCoordinatorTests
~~~

Expected: all ordering, cancellation, lock, and duplicate-prevention tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Conversation/ConversationTurnGate.cs backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs
git commit -m "feat: add ASP.NET conversation coordinator"
~~~

### Task 5: Implement the direct /chat WebSocket endpoint

**Files:**
- Create: backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs
- Modify: backend-dotnet/RuBackend/Program.cs
- Modify: backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
- Create: backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs

- [ ] **Step 1: Write failing endpoint tests**

Use an in-memory WebSocket client and fake coordinator to test:

- missing/invalid token closes with 4401;
- malformed JSON emits error;
- non-object frames emit error;
- oversized frame emits Message too large;
- blank prompts are ignored;
- valid request forwards one ChatRequest;
- coordinator events are serialized in order;
- disconnect cancels the coordinator token.

Run the focused endpoint tests. Expected: FAIL because the direct endpoint is absent.

- [ ] **Step 2: Implement ChatWebSocketEndpoint**

Keep query-token authentication exactly as the current client uses it. Accept text frames only, enforce 256 KiB message bytes and 16,000-character prompt limits, parse session_id as optional string, and call IConversationCoordinator.HandleAsync. Serialize each ChatEvent with camelCase and send it as text. Stop sending after done/error. Cancel the linked token on disconnect and await coordinator cleanup.

- [ ] **Step 3: Map ownership without breaking relay**

Add RuBackend:Ownership:Chat with python, shadow, and aspnet. For python, retain the existing relay route. For aspnet, map /chat to ChatWebSocketEndpoint before the catch-all. For shadow, keep Python as public response and run a deterministic event/hash comparison only when the worker fixture is synthetic; never duplicate a real model generation.

- [ ] **Step 4: Run endpoint, existing GUI client, and route tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ChatWebSocketEndpointTests
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Chat
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/check_aspnetcore_route_matrix.py
~~~

Expected: direct endpoint and existing client tests pass; route checker still reports two WebSocket routes.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests backend-dotnet/compatibility-route-matrix.json docs/aspnetcore-route-matrix.md
git commit -m "feat: add ASP.NET chat WebSocket ownership seam"
~~~

### Task 6: Run live direct-chat and rollback gates

**Files:**
- Modify: scripts/aspnetcore_compatibility_harness.py
- Modify: tests/test_aspnetcore_compatibility_tools.py
- Modify: docs/aspnetcore-cutover-validation.md
- Modify: backend-dotnet/README.md
- Modify: README.md

- [ ] **Step 1: Add harness modes and deterministic assertions**

Add a direct-chat mode that starts the Python worker protocol with a deterministic fake model response, starts ASP.NET with Chat ownership aspnet, sends a real WebSocket session/prompt, and records only event types, counts, sequence numbers, error codes, child exit status, and port cleanup. Keep the existing FastAPI mode for rollback.

- [ ] **Step 2: Run the isolated direct-chat gate**

~~~powershell
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_aspnetcore_compatibility_tools.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/aspnetcore_compatibility_harness.py --chat-owner aspnet --isolated
~~~

Expected: zero failures, ordered session/reset/chunk/done events, one assistant history entry, one memory write, no metadata leakage, and all child processes terminated.

- [ ] **Step 3: Run the rollback gate**

Start the same app with Chat ownership python, send the same synthetic request through the existing FastAPI relay, and compare only event-type/sequence/error-code summaries. The Python path must remain usable after the ASP.NET path is stopped.

- [ ] **Step 4: Update docs and route ownership**

Record direct-chat and rollback evidence. Mark only /chat as direct ASP.NET ownership when the live gate passes. Do not mark voice, VTS, observation, or FastAPI removal complete.

- [ ] **Step 5: Commit**

~~~powershell
git add scripts/aspnetcore_compatibility_harness.py tests/test_aspnetcore_compatibility_tools.py docs/aspnetcore-cutover-validation.md backend-dotnet/README.md README.md
git commit -m "docs: record ASP.NET chat ownership gate"
~~~

## Self-review checklist

- [ ] Every public event, lock, memory/session ordering, privacy, VTS isolation, cancellation, relay fallback, and live-gate requirement maps to a task above.
- [ ] Scan this plan for placeholder markers and vague steps; none may remain.
- [ ] Confirm ChatRequest, ChatEvent, IConversationCoordinator, IMetadataPolicy, IStreamSanitizer, and IWorkerClient method signatures are identical in every task.
- [ ] Run git diff --check after every plan edit.
