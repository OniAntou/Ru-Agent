# Ru Coding-Loop Parity Phase 1: Harness Foundation and Headless Read-Only Tracer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish the durable .NET-owned Agent loop, additive one-step Worker v2, authenticated API/SSE, Public Host process ownership, and a CLI read-only tracer while keeping production Agent v1 as the default.

**Architecture:** Add BCL-only Core plus a separate SQLite adapter; route a complete Agent run through Core; retain Worker v1 for ordinary Chat; make Public Host own Worker/Broker and expose one authenticated runtime that Desktop and CLI can attach to; advertise only existing Broker-backed read-only file capabilities at this checkpoint.

**Tech Stack:** .NET 10, ASP.NET Core, Microsoft.Data.Sqlite 10.0.11, xUnit, Python 3.11/3.13, pytest, SQLite WAL, AES-GCM, Windows DPAPI, SSE, Windows Job Objects, WinUI 3.

---

## Checkpoint outcome

At the end of this phase:

- Core owns one durable run from creation through final history delivery.
- Worker v2 performs exactly one model step and returns a provider-neutral `AssistantStep`.
- A feature-flagged Agent v2 run may use `files.list` and `files.read` through the existing Broker authority.
- Public Host, not WinUI, owns Worker/Broker children in one Job Object.
- CLI can start or attach, create/trust/observe/cancel/continue a read-only run, and render safe NDJSON.
- The 13 API routes exist and canonical route count is 54 HTTP plus 2 WebSocket; policy/approval/reconcile endpoints fail closed with stable `phase_not_available` until their owning phase activates them.
- Ordinary Chat and the existing Agent v1 path remain regression-tested, with `RuBackend:AgentHarness:Version=v1` still the default.

This checkpoint is not coding-loop parity. File mutations, reusable permission rules, terminal, Git, and Desktop Agent cutover remain disabled.

## Task 1: Scaffold the bounded project graph

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Core/RuAgent.Harness.Core.csproj`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/ProjectBoundaryTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/RuAgent.Harness.Persistence.csproj`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj`.
- Create `backend-dotnet/RuAgent.Hosting/RuAgent.Hosting.csproj`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/RuAgent.Hosting.Tests.csproj`.
- Create `backend-dotnet/RuAgent.Client/RuAgent.Client.csproj`.
- Create `backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj`.
- Create `backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj`.
- Create `backend-dotnet/RuAgent.Cli/Program.cs` as a zero-behavior scaffold entrypoint; Task 10 replaces it with the real parser/commands.
- Create `backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj`.
- Modify `backend-dotnet/RuBackend/RuBackend.csproj`.
- Modify `gui-winui3/RuApp.csproj`.

- [x] Verify the worktree and baseline before edits:

```powershell
git -C D:\LLM status --short --branch
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~Agent|FullyQualifiedName~SessionStore|FullyQualifiedName~PublicRouteContract"
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_worker_protocol.py D:\LLM\tests\test_worker_server.py D:\LLM\tests\test_worker_agent_protocol.py
```

Expected: clean/disclosed worktree and zero failures. Run the .NET and Python commands sequentially.

- [x] Create SDK-style projects targeting `net10.0`; test projects use the same xUnit package versions as existing test projects. `RuAgent.Harness.Persistence` references Core and `Microsoft.Data.Sqlite` 10.0.11. `RuAgent.Cli` has `OutputType=Exe`. Do not add `System.CommandLine`; keep parsing explicit and bounded.
- [x] Add project references exactly as fixed in the program index. Do not reference `RuBackend` or `RuApp` from Core, Persistence, Client, or CLI.
- [x] Add one architecture test in `RuAgent.Harness.Core.Tests/ProjectBoundaryTests.cs` that parses `RuAgent.Harness.Core.csproj` and rejects `FrameworkReference`, `PackageReference`, and any `ProjectReference`.
- [x] Build each new empty project sequentially:

```powershell
dotnet build D:\LLM\backend-dotnet\RuAgent.Harness.Core\RuAgent.Harness.Core.csproj -c Release --nologo
dotnet build D:\LLM\backend-dotnet\RuAgent.Harness.Persistence\RuAgent.Harness.Persistence.csproj -c Release --nologo
dotnet build D:\LLM\backend-dotnet\RuAgent.Hosting\RuAgent.Hosting.csproj -c Release --nologo
dotnet build D:\LLM\backend-dotnet\RuAgent.Client\RuAgent.Client.csproj -c Release --nologo
dotnet build D:\LLM\backend-dotnet\RuAgent.Cli\RuAgent.Cli.csproj -c Release --nologo
```

Expected: five successful builds and no dependency-cycle warning.

- [x] Commit only scaffold files: `build(agent): scaffold harness project boundaries`.

## Task 2: Implement explicit Core identities and state machines

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Core/HarnessIdentifiers.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/HarnessRunState.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/HarnessToolCallState.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/HistoryWaitKind.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/TransitionResult.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/HarnessRunStatus.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/HarnessRunTransition.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/ToolCallTransition.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/AgentPermissionPreset.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/IIdGenerator.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/CryptographicIdGenerator.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/AgentRun.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/HarnessEvent.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Workspace/WorkspaceIdentity.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Workspace/WorkspaceTrustDecision.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Workspace/WorkspaceTrustRequest.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Policy/PolicyDecision.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Policy/PolicyRuleScope.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Policy/PolicyRule.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Policy/PolicyMatcher.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Policy/PolicySnapshot.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Policy/PolicySnapshotCanonicalizer.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/AgentRunStateMachine.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/ToolCallStateMachine.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunStateMachineTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunProjectionTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/HarnessEventTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/ToolCallStateMachineTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/HarnessIdentifierTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/WorkspaceTrustStateTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/PolicySnapshotCanonicalizerTests.cs`.

- [x] Write RED theory tests covering every legal run transition from the approved design. Persist `HistoryWaitKind.UserBeforeFirstStep` versus `FinalBeforeCompletion` and prove acknowledgement advances only to `Running` or `Completed` respectively. Reject at least: `Completed -> Running`, `WaitingApproval -> Completed`, `WaitingUser -> Completed`, mismatched history acknowledgement, and `ReconciliationRequired -> Running` without a typed reconcile command.
- [x] Add RED tool-call tests for the exact branches: `Proposed -> Denied` terminal; `Proposed -> Allowed -> Executing -> Succeeded|Failed|TimedOut|Cancelled|OutcomeUnknown`; and `Proposed -> AwaitingApproval -> Denied` or consumed approval `-> Allowed -> Executing`. A typed expiry alone may return `AwaitingApproval -> Proposed` for re-evaluation with a fresh approval ID. Reject `AwaitingApproval -> Executing` without a consumed approval and every transition out of a terminal tool state.
- [x] Add ID validation tests: non-empty bounded values, correct prefixes from `IIdGenerator`, and no implicit conversion between run/tool/execution IDs.
- [x] Add workspace trust state tests for opaque request identity, exact displayed identity-hash binding, idempotent repeated same decision, stale/different response rejection, and denial becoming terminal before any model step.
- [x] Add a phase-1 canonical snapshot test proving preset/workspace/version/empty ordered-rule list produce deterministic canonical UTF-8 JSON and SHA-256; any changed field changes the hash.
- [x] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~StateMachine|FullyQualifiedName~Identifier|FullyQualifiedName~WorkspaceTrust|FullyQualifiedName~PolicySnapshot"
```

Expected RED: compile failures for the missing domain types, then assertion failures until all legal edges are encoded.

- [x] Implement the two enums and state machines as total functions returning a typed `TransitionResult`; do not expose a public mutable `State` setter.
- [x] Implement `readonly record struct` IDs and an immutable `AgentRun` projection with `Version`, `LastSequence`, budget counters, nullable `HistoryWaitKind`, preset snapshot, policy snapshot version/hash, timestamps, and stable terminal error code.
- [x] Run GREEN with the same command. Expected: zero failures.
- [x] Commit: `feat(agent): add explicit harness state machines`.

## Task 3: Implement Core ports, commits, and the one-step outer loop

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Core/AssistantStep.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/ModelStepRequest.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Context/ModelContextEntry.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Context/ModelContextSnapshot.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Context/IModelContextSource.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Context/AgentContextAssembler.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/ToolContracts.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessCommit.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessEventIntent.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessPayloadIntent.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Persistence/ConversationOutboxIntent.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessStoreResults.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/IModelStepRunner.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/IToolGateway.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/IHarnessEventStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/ITimeProvider.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/ToolCallScheduler.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/Fakes/FakeModelStepRunner.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/Fakes/FakeToolGateway.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/Fakes/InMemoryHarnessEventStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/AgentBudgetTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/ToolCallSchedulerTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/AgentContextAssemblerTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/HarnessCommitContractTests.cs`.

- [ ] Add RED tests proving the first model step cannot start before a durable user-history acknowledgement; the final answer leaves the run in `WaitingHistory` until its assistant outbox item is acknowledged.
- [ ] Add RED context tests: immutable session-base entries precede run-local entries; assistant text/tool calls and typed tool results follow durable sequence order; restart produces byte-equivalent provider-neutral input; duplicate source IDs fail closed; no UI/Worker DTO enters the assembler; no durable entry is silently dropped.
- [ ] Add RED commit-contract tests requiring immutable event/payload/outbox lists, unique intent IDs, bounded payload kind/audience/cap metadata, `RecoveryOpaque` internal-only semantics, and rejection when a safe event embeds private bytes/text.
- [ ] Add RED tests proving one `AssistantStep` may contain several calls, independent read-only calls may overlap, and mutations/terminal calls remain serialized in model order.
- [ ] Add RED tests for 32-step/64-call exhaustion entering `WaitingUser`, approval waiting not consuming either budget, and Continue opening a fresh window without creating a new run.
- [ ] Add RED crash-boundary tests: side-effect-free gateway preparation occurs before `tool.execution_requested`; preparation payloads and that event commit atomically before dispatch; a receipt is committed before another model step; a mutation transport failure after dispatch becomes `ReconciliationRequired` and never reuses its execution ID.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Coordinator|FullyQualifiedName~Budget|FullyQualifiedName~Scheduler|FullyQualifiedName~ContextAssembler|FullyQualifiedName~HarnessCommit"
```

Expected RED: missing contracts/coordinator, then ordering assertions fail.

- [ ] Implement `AssistantStep(Text, ToolCalls, FinishReason, EstimatedInputTokens)`, provider-neutral context entries/assembler, `ToolOutputChunk`, and immutable proposed-call/receipt records. Generate model-step, tool-call, execution, approval, event, and payload IDs in Core, never in UI. Core streaming types remain independent of Broker/public wire DTOs.
- [ ] Implement the compare-and-swap `HarnessCommit` boundary from the program index. Every state change emits a typed safe event intent; no raw prompt, arguments, output, or diff enters `HarnessEventIntent`.
- [ ] Implement a bounded channel for transient deltas. Dropped transient frames increment a safe counter but never block Worker/Broker draining or durable commits.
- [ ] Implement read-only retry at most twice with the same execution ID and never retry a mutation after dispatch.
- [ ] Run GREEN. Expected: zero failures and deterministic event sequences.
- [ ] Commit: `feat(agent): own the model tool loop in core`.

## Task 4: Add the separate SQLite Harness store

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteOptions.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteSchema.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/SqliteHarnessEventStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Workspace/SqliteWorkspaceStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Workspace/WorkspaceTrustRequestStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Policy/AgentSessionPermissionStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/HarnessStoreException.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessSqliteSchemaTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessEventStoreTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessProjectionTransactionTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessConcurrencyTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/WorkspaceStoreTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/AgentSessionPermissionStoreTests.cs`.
- Modify `backend-dotnet/RuBackend/Business/DataPaths.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Business/DataPathsTests.cs`.

- [ ] Add computed path tests before implementation:

```csharp
Assert.Equal(root, paths.BusinessDataRoot);
Assert.Equal(Path.Combine(root, "agent_harness.db"), paths.AgentHarnessDatabase);
```

Keep the existing six-argument and eight-argument constructors source-compatible.

- [ ] Write RED schema tests for WAL, the design's eight minimum tables (`agent_runs`, `agent_events`, `agent_payloads`, `agent_conversation_outbox`, `agent_global_rules`, `agent_project_rules`, `agent_run_rules`, `agent_workspaces`), and the required `agent_session_settings` table for per-session preset/version validation.
- [ ] Write RED transaction tests proving event append plus run projection plus payload/outbox insert all commit or all roll back; enforce unique `(run_id, sequence)` and compare-and-swap `version`.
- [ ] Write RED concurrency tests for unique active session and active workspace partial indexes, idempotent `(session_id, idempotency_key)` run creation, and `workspace_busy` without a second insert.
- [ ] Write RED workspace tests for encrypted canonical root, volume/root identity fields, opaque trust request, identity-hash binding, same-response idempotency, moved identity requiring a new request, and Full Access never bypassing trust.
- [ ] Write RED session-permission tests: missing selection plus first Standard run atomically creates version 1; subsequent run must match preset/version; Full can never be created implicitly; new session defaults independently; deleting a session removes its selection.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~DataPaths"
```

Expected RED: schema/store types missing, then constraint/rollback assertions fail.

- [ ] Implement migration version 1 with foreign keys on, busy timeout, WAL, bounded text/blob columns enforced in code, UTC timestamps, and partial unique active-run indexes. Do not reuse `memory.db`.
- [ ] Implement idempotent initialization and store health reporting. A failed Harness database disables Agent endpoints only; it must not prevent ordinary backend startup.
- [ ] Run GREEN sequentially. Expected: zero failures.
- [ ] Commit: `feat(agent): persist runs and events in sqlite`.

## Task 5: Encrypt private payloads and deliver Chat history idempotently

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Persistence/Privacy/IHarnessKeyStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Privacy/WindowsDpapiKeyStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Privacy/HarnessPayloadCryptography.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Privacy/KnownSecretRedactor.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/HarnessPayloadStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/HarnessOutboxStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Context/SqliteModelContextSource.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessPayloadStoreTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/KnownSecretRedactorTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessOutboxTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/ModelContextSourceTests.cs`.
- Modify `backend-dotnet/RuBackend/Business/Sessions/SessionDocument.cs`.
- Modify `backend-dotnet/RuBackend/Business/Sessions/SessionStore.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Business/SessionStoreTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Business/HistoryStoreTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs`.
- Create `backend-dotnet/RuBackend/Harness/AgentConversationOutboxDispatcher.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessModelContextBootstrapper.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessSessionDeletionCoordinator.cs`.
- Modify `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentOutboxDeliveryTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/HarnessModelContextBootstrapperTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs`.

- [ ] Write RED AES-GCM tests: ciphertext differs for equal plaintext due to nonce; wrong run/payload associated data fails; tampering fails; size caps reject before allocation.
- [ ] Write RED DPAPI/key-file tests behind `OperatingSystem.IsWindows()`: one random 256-bit key persists, unprotects only in CurrentUser scope, and key/database ACLs contain no broad write principal. Use an injected fake key store for non-Windows unit coverage.
- [ ] Write RED streaming-redactor tests with secrets split across chunk boundaries and verify safe events, encrypted payload plaintext after decryption, SSE frames, and diagnostic captures contain no known secret. Add an explicit test documenting that an unknown arbitrary value is not promised detectable.
- [ ] Add RED payload-kind tests: each intent has a fixed size cap and audience; model/client/terminal text uses redaction; opaque internal recovery bytes are rejected by public/model readers and remain byte-identical through encrypt/decrypt rather than passing through a text redactor.
- [ ] Extend `SessionMessage` with optional `source_message_id` and `source_run_id`; add RED tests that legacy JSON loads, `AppendIfAbsentAsync` returns `Appended` then `AlreadyPresent`, and a retry after append-before-outbox-ack creates one visible message.
- [ ] Add RED context-source tests: after the user outbox acknowledgement, capture the current bounded `GetContextTurnsAsync` result once as encrypted `model_context_base` payload entries with stable source IDs; append run-local context payload intents atomically with their durable events; restart/decrypt reconstructs the same ordered snapshot; deleted/wrong-run/tampered payload fails closed and no plaintext enters logs.
- [ ] Add RED dispatcher tests for transient retry, deleted/invalid session failing before first model step, final answer remaining `WaitingHistory`, and session deletion cancelling/removing Harness records before deleting the session file.
- [ ] Add RED atomicity tests: an already-trusted run creates its user payload/user outbox/`WaitingHistory` together; an untrusted run stores the encrypted prompt in `WaitingWorkspaceTrust` and only an accepted trust decision atomically creates the user outbox plus `WaitingHistory`; final model event/final visible payload/assistant outbox/`WaitingHistory` commit together. Only stable-ID append acknowledgement advances to `Running` or terminal `Completed`. Crash at every boundary must produce zero or one visible message, never a model/tool replay caused by history delivery.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Payload|FullyQualifiedName~Redactor|FullyQualifiedName~Outbox|FullyQualifiedName~ModelContext"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~SessionStore|FullyQualifiedName~AgentOutboxDelivery|FullyQualifiedName~HarnessModelContext"
```

Expected RED: privacy/outbox contracts absent, then duplicate-delivery assertions fail.

- [ ] Implement encryption, source-ID compatibility, outbox claim/ack/retry, bounded exponential retry, and the context bootstrap/source. Store visible/context text only as encrypted payloads; safe events and the outbox reference opaque payload/source IDs.
- [ ] Ensure `GetContextTurnsAsync` ignores source metadata and preserves current eight-turn behavior.
- [ ] Run GREEN sequentially. Expected: zero failures.
- [ ] Commit: `feat(agent): protect payloads and dedupe history`.

## Task 6: Add an additive one-step Worker v2

**Files:**

- Create `src/ru_runtime/worker/v2/__init__.py`.
- Create `src/ru_runtime/worker/v2/protocol.py`.
- Create `src/ru_runtime/worker/v2/model_step.py`.
- Create `src/ru_runtime/worker/v2/handlers.py`.
- Create `src/ru_runtime/worker/server.py`.
- Modify `src/ru_runtime/worker/v1/server.py` only to expose its v1 connection handler to the multiplexer; do not change v1 Chat semantics.
- Create `tests/test_worker_v2_protocol.py`.
- Create `tests/test_worker_v2_step.py`.
- Create `tests/test_worker_v2_server.py`.
- Modify `tests/test_worker_server.py` only for the shared server entrypoint.

- [ ] Write RED protocol tests for authenticated `/worker/v2` hello and these messages only: `agent.step.start`, `agent.step.delta`, `agent.step.done`, `agent.step.cancel`, `error`.
- [ ] Define the v2 result shape in tests:

```python
@dataclass(frozen=True, slots=True)
class AssistantStep:
    text: str | None
    tool_calls: tuple[ModelToolCall, ...]
    finish_reason: Literal["stop", "tool_calls", "length"]
```

`ModelToolCall` contains optional provider call ID, `tool_id`, `tool_version`, and bounded object arguments. It never contains a receipt or approval token.

- [ ] Add RED tests that one request invokes `RuntimeRunner.generate_reply` exactly once; a structured selection becomes one tool call; prose becomes text; braced selection JSON never streams as visible delta; cancellation cancels the single generation.
- [ ] Add RED failure/privacy tests: provider timeout/unavailable/protocol failure emits only a stable bounded Worker error code and correlation IDs; provider body, URL query, credentials, prompt, and exception message never enter Worker logs or public/safe frames.
- [ ] Add a source-contract test that `src/ru_runtime/worker/v2` contains no tool-result queue, receipt wait, approval request, Broker import, event-store import, or outer `MAX_TOOL_CALLS_PER_TURN` loop.
- [ ] Run RED:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_worker_v2_protocol.py D:\LLM\tests\test_worker_v2_step.py D:\LLM\tests\test_worker_v2_server.py
```

Expected RED: v2 package/messages missing.

- [ ] Implement the shared server multiplexer: `/worker/v1` delegates untouched v1 behavior; `/worker/v2` uses independent connection state and never accepts `chat.tool.result`.
- [ ] Reuse the current provider/model adapter and structured-selection parser, but perform exactly one generation. Bound messages, tools, schemas, arguments, text, and deltas at protocol ingress/egress.
- [ ] Run GREEN, then run v1 regression:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_worker_v2_protocol.py D:\LLM\tests\test_worker_v2_step.py D:\LLM\tests\test_worker_v2_server.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_worker_action_loop.py D:\LLM\tests\test_worker_agent_protocol.py D:\LLM\tests\test_worker_protocol.py D:\LLM\tests\test_worker_server.py D:\LLM\tests\test_tool_calls.py
```

Expected: both commands pass.

- [ ] Commit: `feat(worker): add one-step agent protocol v2`.

## Task 7: Adapt Worker and Broker read-only ports in ASP.NET Core

**Files:**

- Create `backend-dotnet/RuAgent.Protocol/Harness/WorkerV2Contracts.cs`.
- Create `backend-dotnet/RuAgent.Protocol/AgentProtocolVersions.cs`.
- Create `backend-dotnet/RuAgent.Protocol/Harness/BrokerRunLeaseContracts.cs`.
- Create `backend-dotnet/RuAgent.Protocol/Harness/BrokerPolicyContracts.cs`.
- Modify `backend-dotnet/RuAgent.Protocol/AgentEnvelopeCodec.cs`.
- Modify `backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs`.
- Create `backend-dotnet/RuAgent.Broker/Runtime/BrokerRunLease.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs`.
- Create `backend-dotnet/RuBackend/Harness/Worker/WorkerV2Envelope.cs`.
- Create `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`.
- Create `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessRunService.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessFeatureOptions.cs`.
- Create `backend-dotnet/RuBackend/Harness/Workspace/WindowsWorkspaceIdentityResolver.cs`.
- Create `backend-dotnet/RuBackend/Harness/Workspace/WorkspaceTrustService.cs`.
- Create `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ContractTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/HarnessReadOnlyTracerTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/WorkspaceIdentityResolverTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/WorkspaceTrustEndpointTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/AgentEnvelopeTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs`.
- Modify `backend-dotnet/RuBackend/Program.cs`.

- [ ] Write RED C# codec tests against the same golden JSON fixtures used in `tests/test_worker_v2_protocol.py`; reject wrong version, duplicate sequence, oversized schema/arguments, tool-result messages, and terminal fields.
- [ ] Write RED client tests proving one `RunStepAsync` request maps deltas and exactly one final `AssistantStep`, and cancellation emits `agent.step.cancel` before closing.
- [ ] Write RED Broker compatibility tests: v1 behavior remains valid; v2 requires `run.open` with run/session/workspace ID, canonical root identity, preset, snapshot version/hash, and expiry before advertising capabilities; v1 rejects v2-only messages.
- [ ] Write RED lease tests proving the read-only tracer root comes only from the trusted run lease, never Broker cwd or `RU_WORKSPACE_ROOT`; identity/hash mismatch fails closed; phase-1 snapshot advertises only Broker-backed `files.list`/`files.read`.
- [ ] Define the two-phase v2 skeleton now: `tool.evaluate` returns an authoritative decision/canonical intent token, side-effect-free `tool.prepare` revalidates it, and `tool.execute` consumes it. This lets Core persist `tool.execution_requested` before any adapter call. Approval and reusable-rule branches return `phase_not_available` until phase 2.
- [ ] Write RED tracer tests with fake Worker and current `IAgentBrokerClient`: advertise only executable `files.list`/`files.read`; persist proposal/request/receipt events; return typed denial for any mutation or terminal proposal; deliver final visible answer once.
- [ ] Add a capability-boundary test proving `trust`, approval responses, policy/session-preset management, cancel, Continue, payload fetch, and reconcile are authenticated user-control API actions only and can never appear in the Worker model-visible tool catalog or be invoked from model arguments.
- [ ] Write RED Windows workspace tests for canonical final root, volume serial/root file ID, stable display identity hash, moved directory requiring trust again, repository markers granting nothing, trust approve/deny idempotency, stale/replayed response, and no model step before approval.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~WorkerV2|FullyQualifiedName~HarnessReadOnlyTracer"
```

Expected RED: adapters/services missing.

- [ ] Implement `HarnessFeatureOptions.Version` with strict `v1|v2` parsing and default `v1`. Snapshot the chosen value in `agent_runs`; never consult the flag again for that run.
- [ ] Implement the Worker adapter separately from the existing 800-line `LoopbackWorkerClient`; do not add v2 branches to that class.
- [ ] Implement `HarnessToolGateway` as a transport adapter, not a second policy engine. It uses Broker-advertised capabilities and the existing typed receipt checks, while phase capability exposure limits this tracer to list/read.
- [ ] Implement dual-version Broker parsing and a v2 run lease. The phase-1 immutable Standard snapshot has an empty explicit-rule list and a canonical hash; Broker's authoritative fallback allows only list/read for the tracer. Phase 2 extends this same contract/evaluator rather than replacing it.
- [ ] Register Core/store/adapters so a Harness-store health failure disables only `/agent/v1/*` with `agent_store_unavailable`.
- [ ] Run GREEN. Expected: zero failures and v1 `ConversationCoordinatorTests` unchanged.
- [ ] Commit: `feat(agent): wire read-only core adapters`.

## Task 8: Expose the complete Agent API and reconnectable SSE

**Files:**

- Create `backend-dotnet/RuAgent.Protocol/Harness/AgentApiContracts.cs`.
- Create `backend-dotnet/RuAgent.Protocol/Harness/HarnessEventContracts.cs`.
- Create `backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs`.
- Create `backend-dotnet/RuBackend/Harness/AgentV1Service.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessSseWriter.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessLiveEventHub.cs`.
- Create `backend-dotnet/RuBackend/Harness/HarnessDiagnosticEventFactory.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentV1EndpointTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentSseReconnectTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentRunIdempotencyTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentSessionRunsTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/HarnessDiagnosticEventFactoryTests.cs`.
- Modify `backend-dotnet/RuBackend/Program.cs`.
- Modify `backend-dotnet/RuBackend/Diagnostics/DiagnosticEvent.cs`.
- Modify `backend-dotnet/RuBackend/Diagnostics/DiagnosticEventPolicy.cs`.
- Modify `backend-dotnet/RuBackend/Diagnostics/DiagnosticCodes.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEventTests.cs`.
- Modify `backend-dotnet/public-route-contract.json`.
- Modify `scripts/check_aspnetcore_route_matrix.py`.
- Modify `tests/test_aspnetcore_route_matrix.py`.
- Modify `backend-dotnet/RuBackend.Tests/PublicRouteContractTests.cs`.
- Modify `docs/aspnetcore-route-matrix.md` only to document the additive route contract and that Agent v2 is still feature-flagged.

- [ ] Lock the additive route manifest to these 13 exact method/path pairs and add RED bearer-rejection tests for every one: `POST /agent/v1/runs`; `GET /agent/v1/runs/{runId}`; `GET /agent/v1/runs/{runId}/events`; `GET /agent/v1/runs/{runId}/payloads/{payloadId}`; `POST /agent/v1/runs/{runId}/approvals/{approvalId}`; `POST /agent/v1/runs/{runId}/trust`; `POST /agent/v1/runs/{runId}/cancel`; `POST /agent/v1/runs/{runId}/continue`; `POST /agent/v1/runs/{runId}/reconcile`; `GET /agent/v1/sessions/{sessionId}/runs`; `GET /agent/v1/policies`; `POST /agent/v1/policies`; `DELETE /agent/v1/policies/{ruleId}`.
- [ ] Define run creation with `session_id`, workspace request, prompt, `preset`, optional `preset_selection_version`, and idempotency key. Only a missing first Standard selection may omit the version; every existing selection and every Full request must match the stored session version.
- [ ] Add RED status tests: create returns 201; exact idempotent replay returns 200 with the same run; the same key with a different normalized request returns 409 `idempotency_conflict`; active-workspace conflict returns 409 `workspace_busy`; missing/invalid IDs return bounded 400/404; first Standard selection is created atomically and later preset/version mismatch returns 409 `preset_mismatch`; trust is fully functional; unavailable policy-rule/approval/reconcile actions return 409 `phase_not_available`, never 501 with raw exception text.
- [ ] Add RED session-history tests proving `GET /agent/v1/sessions/{sessionId}/runs` returns that session's active and terminal runs in deterministic `(created_at DESC, run_id DESC)` order and never leaks another session. Lock query/response pagination to `limit` default 50, range 1..100, optional opaque `cursor`, `items`, and nullable `next_cursor`; reject malformed/foreign cursors without exposing their contents.
- [ ] Add RED safe-envelope schema tests requiring `protocol_version`, `event_id`, `run_id`, `session_id`, durable `sequence`, `event_type`, `occurred_at`, `status`, bounded `safe_summary`, and optional opaque `payload_ref`; reject unknown protocol versions and prove forbidden raw command/argument/path/code/diff/output/provider/control fields never serialize.
- [ ] Add RED diagnostic-policy/factory tests for optional bounded `run_id`, `model_step_id`, `tool_call_id`, `execution_id`, `duration_ms`, `byte_count`, and `truncated` fields plus stable Harness event/error codes. Feed raw prompt, command, argv, path, code, diff, stdout/stderr, provider body, environment, and exception messages at the factory boundary and prove none can enter serialized diagnostic JSONL.
- [ ] Add RED SSE tests for durable `id: <sequence>`, non-negative `Last-Event-ID`, non-negative `after_sequence`, equal-value acceptance and conflicting dual-cursor `cursor_mismatch`, reconnect from every durable sequence, streaming replay without materializing all events, terminal final snapshot/clean close, transient frames with connection-local sequence and no durable cursor advance, and bounded-channel frame loss not blocking final receipt persistence.
- [ ] Add RED payload tests proving run/payload binding, on-demand decryption only for authenticated-client audience, bearer enforcement, no payload content in event JSON, and 404 for another run's payload ID or any internal `RecoveryOpaque` payload.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentV1|FullyQualifiedName~AgentSse|FullyQualifiedName~AgentRunIdempotency|FullyQualifiedName~AgentSessionRuns|FullyQualifiedName~HarnessDiagnostic|FullyQualifiedName~DiagnosticEventPolicy|FullyQualifiedName~PublicRouteContract"
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
```

Expected RED: routes absent and checker reports 41 rather than 54 HTTP routes.

- [ ] Implement thin endpoint mapping and DTO-to-Core translation. Keep policy, approval, and reconciliation routes present but fail closed until phase 2/3 services are active. Route Harness diagnostics only through `HarnessDiagnosticEventFactory`; never pass arbitrary dictionaries or exception messages to the structured writer.
- [ ] Update canonical route sources atomically to exactly 54 HTTP plus 2 WebSocket routes. Preserve every pre-existing method/path.
- [ ] Run GREEN sequentially. Expected: endpoint tests pass and checker prints `54 HTTP + 2 WebSocket`.
- [ ] Commit: `feat(agent): expose durable harness api`.

## Task 9: Move Worker/Broker ownership below WinUI

**Files:**

- Create `backend-dotnet/RuAgent.Hosting/PublicHostRuntimeOptions.cs`.
- Create `backend-dotnet/RuAgent.Protocol/Harness/RuntimeDescriptorContracts.cs`.
- Create `backend-dotnet/RuAgent.Hosting/PublicHostSingleInstance.cs`.
- Create `backend-dotnet/RuAgent.Hosting/WorkerBrokerLaunchPlan.cs`.
- Create `backend-dotnet/RuAgent.Hosting/ChildProcessPolicy.cs`.
- Create `backend-dotnet/RuAgent.Hosting/OwnedProcessTree.cs`.
- Create `backend-dotnet/RuAgent.Hosting/WorkerBrokerProcessManager.cs`.
- Create `backend-dotnet/RuAgent.Hosting/RuntimeDescriptorWriter.cs`.
- Create `backend-dotnet/RuAgent.Hosting/RuntimeIdleMonitor.cs`.
- Create `backend-dotnet/RuAgent.Hosting/RuntimeClientLeaseRegistry.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/WorkerBrokerLaunchPlanTests.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/ChildProcessPolicyTests.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/OwnedProcessTreeTests.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/RuntimeDescriptorTests.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/PublicHostSingleInstanceTests.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/RuntimeIdleMonitorTests.cs`.
- Create `backend-dotnet/RuAgent.Hosting.Tests/RuntimeClientLeaseRegistryTests.cs`.
- Create `backend-dotnet/RuAgent.Client/PublicHostLocator.cs`.
- Create `backend-dotnet/RuAgent.Client/PublicHostStarter.cs`.
- Create `backend-dotnet/RuAgent.Client/PublicHostSession.cs`.
- Create `backend-dotnet/RuAgent.Client/RuntimeDescriptorValidator.cs`.
- Create `backend-dotnet/RuAgent.Client/SessionApiClient.cs`.
- Create `backend-dotnet/RuAgent.Client/AgentApiClient.cs`.
- Create `backend-dotnet/RuAgent.Client/AgentSseClient.cs`.
- Create `backend-dotnet/RuAgent.Client/AgentApiException.cs`.
- Create `backend-dotnet/RuAgent.Client.Tests/PublicHostLocatorTests.cs`.
- Create `backend-dotnet/RuAgent.Client.Tests/PublicHostStarterTests.cs`.
- Create `backend-dotnet/RuAgent.Client.Tests/SessionApiClientTests.cs`.
- Create `backend-dotnet/RuAgent.Client.Tests/AgentApiClientTests.cs`.
- Create `backend-dotnet/RuAgent.Client.Tests/AgentSseClientTests.cs`.
- Modify `backend-dotnet/RuBackend/Program.cs`.
- Modify `gui-winui3/Services/ManagedRuBackendSessionProvider.cs`.
- Modify `gui-winui3/Services/RuAppComposition.cs`.
- Modify `gui-winui3.Tests/BackendLaunchPlanTests.cs`, `gui-winui3.Tests/BackendProcessManagerTests.cs`, `gui-winui3.Tests/BackendChildProcessPolicyTests.cs`, and `gui-winui3.Tests/BackendBrokerLaunchContractTests.cs` by moving reusable assertions to Hosting/Client tests.
- Delete `gui-winui3/Services/BackendLaunchPlan.cs`, `gui-winui3/Services/BackendProcessManager.cs`, `gui-winui3/Services/BackendProcessRuntime.cs`, and `gui-winui3/Services/BackendChildProcessPolicy.cs` only after no production reference remains.

- [ ] Port tests first and make them RED against the new projects: launch plan contains Worker and Broker only; never Public Host; environment includes private child tokens but no Ru/provider secrets; bootstrap summaries are bounded metadata only.
- [ ] Add RED single-instance/descriptor tests: named mutex; current-user ACL; strict versioned descriptor fields `protocol_version`, loopback `base_url`, public bearer, PID, process start time, instance nonce, and creation time; rejection of unknown fields/versions/non-loopback URLs; and proof that Worker/Broker tokens, executable paths, arguments, environment, and provider credentials never serialize. Bind descriptor to PID/start-time/nonce, require authenticated health, prove configured-bearer success plus random wrong-bearer 401/403, reject stale descriptors, replace atomically, and clean up. If the configured public port is occupied without a valid owned descriptor/authenticated health response, start fails immediately with stable `public_host_port_conflict` and never attaches to or kills that process.
- [ ] Add RED shared-client tests for authenticated `POST /sessions/new`, Agent HTTP commands, strict DTO/version parsing, payload run binding, SSE cursor/reconnect, transient frames, and cancellation. CLI uses `SessionApiClient` for its dedicated-session default; CLI and Desktop both consume the shared Agent HTTP/SSE clients rather than implement a second Agent transport stack.
- [ ] Add RED process-tree tests on Windows proving host shutdown kills Worker/Broker child and grandchild processes, while descriptor/database durable state behavior remains correct.
- [ ] Add RED idle/lease tests for the ten-minute default and suppression while an active run, approval, or authenticated long-lived client lease exists. Define client leases concretely as the lifetime of an accepted Chat WebSocket or Agent SSE connection; abrupt disconnect/disposal releases once, ordinary short HTTP calls do not create immortal leases, and fake time makes the timeout deterministic.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj -c Release --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --nologo
```

Expected RED: hosting/client types missing.

- [ ] Implement `RuBackend.exe --managed-runtime` bootstrap before Broker/Worker DI construction. Public Host generates private credentials, starts Worker at the shared server entrypoint and Broker, assigns both to one Job Object, publishes the ACL-restricted descriptor, and wires accepted Chat WebSocket/Agent SSE lifetimes into `RuntimeClientLeaseRegistry`.
- [ ] Implement Client start-or-attach: validate descriptor plus authenticated health; if absent/stale, start exactly one Public Host hidden and poll for a valid descriptor. Never start Worker/Broker from Client.
- [ ] Implement `SessionApiClient`, `AgentApiClient`, and `AgentSseClient` over the validated `PublicHostSession`. `SessionApiClient.CreateAsync` accepts only the current `status=ok` response with a bounded valid `session_id`; keep UI dispatching and CLI rendering out of this library.
- [ ] Switch WinUI backend session provider to Client start-or-attach for both Chat and future Agent. Remove old WinUI ownership files after references and tests are migrated.
- [ ] Run GREEN plus WinUI regression:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj -c Release --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Backend"
```

Expected: zero failures and only Public Host owns Worker/Broker.

- [ ] Commit: `refactor(runtime): move child ownership to public host`.

## Task 10: Add the thin read-only CLI tracer

**Files:**

- Modify `backend-dotnet/RuAgent.Cli/Program.cs`.
- Create `backend-dotnet/RuAgent.Cli/AgentCliParser.cs`.
- Create `backend-dotnet/RuAgent.Cli/AgentCommands.cs`.
- Create `backend-dotnet/RuAgent.Cli/SafeEventRenderer.cs`.
- Create `backend-dotnet/RuAgent.Cli/NdjsonEventRenderer.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/AgentCliParserTests.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/ReadOnlyTracerTests.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/NdjsonRendererPrivacyTests.cs`.
- Create `backend-dotnet/RuAgent.Cli/README.md`.

- [ ] Add RED parser tests for `agent run`, `attach`, `trust`, `cancel`, `continue`, and `status`; reject unknown switches, duplicate mutually exclusive switches, missing workspace/prompt, and `--preset full` in this phase.
- [ ] Add RED behavior tests using a fake HTTP handler: omitted `--session` calls the existing authenticated `POST /sessions/new` through `RuAgent.Client.SessionApiClient`, creates one dedicated session, prints session/run IDs, uses Standard, sends a fresh idempotency key, follows SSE cursor, and Ctrl+C posts cancel before exit.
- [ ] Add RED `--json` tests for versioned NDJSON, no prompts/implicit approvals, no private payload auto-fetch, and absence of prompt/command/output/secret fields.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --nologo
```

Expected RED: parser/commands absent.

- [ ] Implement explicit parsing with a maximum argument count and bounded values. The CLI delegates start/attach to `RuAgent.Client` and all run behavior to HTTP/SSE; it contains no Worker, Broker, model-loop, policy, or adapter code.
- [ ] Make interactive output display only safe event fields. Fetching a private payload requires an explicit later command; the read-only tracer never fetches automatically.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(cli): add headless read-only agent tracer`.

## Task 11: Verify the phase and reconcile documentation

**Files:**

- Modify `build.bat` to restore/build/publish new runtime and CLI projects in deterministic order.
- Modify `.github/workflows/ci.yml` and `.github/workflows/gui-harness.yml` only if their current project/test matrices require the new projects; preserve the Python 3.11/3.13 matrix and do not rename unrelated workflows in this phase.
- Modify `README.md`, `backend-dotnet/README.md`, `docs/architecture.md`, `docs/operations/agent-tools.md`, and `CONTEXT.md` only for process ownership, additive feature-flagged Agent v2, Worker v2, API/SSE, and CLI tracer facts now true.
- Modify `tests/test_current_documentation.py` to require the truthful transitional state and remove assertions that WinUI owns Worker/Broker.
- Modify `docs/README.md` if the plan/checkpoint links are not current.
- Inspect `TODO.md`; retain the separate OS sandbox item unchanged.

- [ ] Search for stale current statements and implementation placeholders:

```powershell
rg -n "WinUI.*owns|RuApp.*owns|41 HTTP|Agent has no terminal|TBD|TODO|FIXME|NotImplementedException" D:\LLM\README.md D:\LLM\backend-dotnet\README.md D:\LLM\CONTEXT.md D:\LLM\docs D:\LLM\backend-dotnet\RuAgent.Harness.Core D:\LLM\backend-dotnet\RuAgent.Harness.Persistence D:\LLM\backend-dotnet\RuAgent.Hosting D:\LLM\backend-dotnet\RuAgent.Client D:\LLM\backend-dotnet\RuAgent.Cli D:\LLM\backend-dotnet\RuBackend\Harness
```

Expected: only dated historical records and explicitly deferred work match; no production placeholder remains.

- [ ] Run all phase tests sequentially:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_current_documentation.py
cmd /c D:\LLM\build.bat
```

Expected: all tests pass, route checker reports 54+2, and build/publish succeeds. Do not run commands concurrently.

- [ ] Run a deterministic fake Worker/Broker tracer through CLI; verify one user/final history delivery, reconnect cursor, cancellation, and no private content in diagnostics. Do not invoke live audio, volume, desktop, microphone, or VTS.
- [ ] Inspect docs for outdated current-operating claims; delete only documents that are genuinely obsolete and retain dated specs/plans as history.
- [ ] Inspect `git diff`, stage only phase-1 files, and commit: `feat(agent): complete headless harness foundation`.
- [ ] Record the final phase-1 test counts and commit hash in the execution log or commit body; do not edit this plan into a false “implemented” claim unless the checkpoint actually passed.
