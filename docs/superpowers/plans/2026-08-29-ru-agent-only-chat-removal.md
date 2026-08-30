# Agent-only runtime: remove Chat transport and mode Implementation Plan

> **Status:** Historical implementation record. The Agent-only transport cut
> shipped on `main`; current behavior is documented in `docs/architecture.md`
> and `docs/operations/runtime-behavior.md`.
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Goal

Make Agent the only Ru text-generation path. The finished application has no public
`/chat` WebSocket, no Chat generation branch in Worker v1, no GUI Chat transport or
mode switch, and no compatibility redirect or feature flag. Text and microphone turns
both create authenticated Agent runs and consume the same Agent SSE stream. Existing
workspace policy, Standard/Full Access presets, approval/trust/cancel/reconnect
semantics, session history, memory retrieval/learning, safe metadata handling, VTS
actions, activity projection, TTS, subtitles, observation, and non-conversation device
protocols remain available.

The public contract after the cut is exactly 54 HTTP method/path pairs and one
WebSocket route, `/voice/stream`. `/voice/transcribe` remains the HTTP transcription
seam; its result is submitted to the Agent path and never opens a second generation
transport. No automatic fallback from Agent to the deleted Chat runtime is allowed.

OpenCode-style system-prompt composition is intentionally outside this change. This
plan only keeps the current Agent context and adds the minimum Agent-owned seams needed
to retain the behavior previously hidden inside `ConversationCoordinator`.

## Architecture

The migration removes one entire module rather than wrapping it. The authenticated
Agent API, `AgentRunCoordinator`, `AgentRuntimeLoopService`, `AgentConversationOutboxDispatcher`,
and `LoopbackModelStepRunner` remain the single execution chain. The old
`ConversationCoordinator` is not replaced by a forwarding façade.

The new seams are narrow and owned by the Agent path:

* `IAgentReplyPolicy` prepares one final reply for persistence: it strips private
  voice/action/VTS markers, validates VTS codes against a catalog, and returns safe
  visible text plus bounded emotion/codes.
* `IAgentReplyEffectSink` performs non-critical post-commit effects (VTS action and
  safe activity metadata) with a run/model-step idempotency key. An effect failure is
  observable through a bounded diagnostic/activity code and never turns a committed
  assistant reply into a failed run.
* `HarnessModelContextBootstrapper` owns memory context capture. It appends a
  model-only system entry containing the same bounded memory rendering used by the old
  turn path, while the outbox dispatcher owns best-effort memory learning after an
  assistant message is durably appended.
* `ITransientContentRedactor` is composed with the incremental private-marker
  sanitizer for Agent assistant deltas. The UI therefore never receives a private
  marker merely because the final model text contains one.
* `AgentRunClientAdapter` parses the existing transient `assistant.delta` envelope into
  a typed callback. Raw tool output and malformed transient frames remain discarded.

Module ownership after the cut:

| Module | Owns | Explicitly does not own |
| --- | --- | --- |
| `RuAgent.Harness.Core` | Agent run state machine, model/tool loop, typed reply-policy/effect seams, transient frames | Worker v1 protocol, WinUI transport, memory database, VTS socket details |
| `RuBackend/Harness` | Reply policy/effects, Agent memory projection, Agent activity projection, Agent runtime composition | A second conversation coordinator or Chat route |
| `RuBackend/Worker` | Runtime/voice/VTS/device adapters and Worker v2 model-step adapter | `WorkerChatRequest`, `GenerateChatAsync`, `chat.*`, Chat-only action outcome transport |
| `RuApp.Services` / `ChatView` | Agent API/SSE subscription, text/voice turn presentation, preset selection | `/chat` WebSocket client, mode branching, Chat queue/presenter |
| Python `worker/v1` | Runtime load/request, STT/voice, VTS, health, observation/device operations | Chat generation, memory-extract protocol, Chat tool-result queues |

The implementation sequence deliberately establishes the Agent reply/memory seams and
red tests before deleting the old module. This keeps the migration local at each seam,
prevents a silent loss of privacy/VTS/memory behavior, and makes every deletion
provable with a negative contract test.

## Tech Stack

* C#/.NET 10, ASP.NET Core Minimal APIs, WinUI 3, `System.Net.WebSockets`, `System.Text.Json`.
* `RuAgent.Harness.Core` + SQLite encrypted payload/outbox persistence.
* Python 3 runtime worker with `pytest`, `ruff`, `mypy`.
* xUnit for backend/client/WinUI projects.
* PowerShell on Windows; heavyweight suites run sequentially with
  `D:\LLM\.venv\Scripts\python.exe -m ...`.

---

## Execution guardrails

- [ ] Work from `D:\LLM` on `main` and inspect `git status --short --branch` before
  every task. Existing dirty files are user work. Do not reset, checkout, stash, or
  stage the whole tree.
- [ ] Before editing a file that is already dirty, save its current diff with
  `git diff -- <path>` and apply only the intended hunks. Every commit uses an explicit
  path list (never `git add -A`); unrelated dirty files remain untouched.
- [ ] Run the focused red/green command for the current task before moving to the next
  task. Commit only the focused files once the task is green, using a descriptive
  conventional-commit message.
- [ ] After each task, inspect `docs/architecture.md`,
  `docs/operations/runtime-behavior.md`, `docs/operations/verification.md`, and
  `CONTEXT.md` for statements made stale by that task. Update the appropriate docs in
  the same task; do not leave a deleted route or mode documented as supported.
- [ ] Keep the future OS-enforced filesystem/network sandbox as a separate project;
  this cut changes the Agent transport only and does not call request flags or
  allowlists a sandbox.

## Task 1: Establish Agent-only contract red tests

**Files**

* Modify `tests/test_aspnetcore_route_matrix.py`.
* Modify `scripts/check_aspnetcore_route_matrix.py`.
* Modify `tests/test_aspnetcore_compatibility_tools.py` only where it asserts the
  public WebSocket set.
* Add `tests/test_agent_only_contract.py`.

### 1.1 Write the failing route assertions first

- [ ] Change `EXPECTED_WEBSOCKET_ROUTES` to exactly
  `{("WS", "/voice/stream")}` and change the validator expectation from `(54, 2)`
  to `(54, 1)`. Keep the 54 HTTP assertion unchanged.
- [ ] Add `test_agent_only_contract_has_no_chat_route` to
  `tests/test_agent_only_contract.py`. It loads
  `backend-dotnet/public-route-contract.json`, asserts there is no object whose
  `path` is `/chat`, asserts the only `transport == "websocket"` object is
  `("WS", "/voice/stream")`, and scans the ASP.NET route source list to ensure
  `ChatWebSocketEndpoint.cs` is not a source for any contract entry.
- [ ] Remove the Chat probe fixture from
  `tests/test_aspnetcore_compatibility_tools.py`; retain the voice-stream and all
  Agent HTTP compatibility fixtures.
- [ ] Update the route checker’s final invariant to `http_count == 54 and
  websocket_count == 1`, with an error message naming `/voice/stream` as the
  expected WebSocket.

### 1.2 Prove the tests are red against the current tree

- [ ] Run
  `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_route_matrix.py tests/test_agent_only_contract.py tests/test_aspnetcore_compatibility_tools.py -q`.
  It must fail because the current contract still contains `/chat` and reports two
  WebSockets. Do not weaken the assertions to make the old implementation pass.
- [ ] Commit only the three test/script files with
  `test: lock Agent-only route contract` after recording the expected red output in
  the task notes (the commit is intentionally red until Task 5 removes the route).

## Task 2: Move final-reply privacy, VTS, memory, and activity behavior behind Agent seams

This task is the safety-first migration. It must be green before any Chat transport
file is deleted.

**Core files**

* Add `backend-dotnet/RuAgent.Harness.Core/AgentReplyContracts.cs`.
* Modify `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`.
* Modify `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs` tests in
  `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs`.

**Backend files**

* Add `backend-dotnet/RuBackend/Harness/AgentReplyPolicy.cs`.
* Add `backend-dotnet/RuBackend/Harness/AgentReplyEffectSink.cs`.
* Add `backend-dotnet/RuBackend/Harness/AgentReplyTransientRedactor.cs`.
* Add `backend-dotnet/RuBackend/Harness/AgentMemoryProjection.cs`.
* Modify `backend-dotnet/RuBackend/Harness/HarnessModelContextBootstrapper.cs`.
* Modify `backend-dotnet/RuBackend/Harness/AgentConversationOutboxDispatcher.cs`.
* Modify `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs` and
  `backend-dotnet/RuBackend/Program.cs` composition.
* Keep the existing metadata behavior from
  `backend-dotnet/RuBackend/Conversation/MetadataPolicy.cs` and
  `backend-dotnet/RuBackend/Conversation/StreamSanitizer.cs` during this task; move
  their implementation into the new Agent files only after tests prove parity.

**Tests**

* Add `backend-dotnet/RuBackend.Tests/Harness/AgentReplyPolicyTests.cs`.
* Add `backend-dotnet/RuBackend.Tests/Harness/AgentReplyEffectSinkTests.cs`.
* Add `backend-dotnet/RuBackend.Tests/Harness/AgentMemoryProjectionTests.cs`.
* Extend `backend-dotnet/RuBackend.Tests/Conversation/MetadataPolicyTests.cs` and
  `StreamSanitizerTests.cs` only as migration parity tests; delete those files in
  Task 5 after the implementation has moved.

### 2.1 Define the typed Core contracts

- [ ] Add the following bounded records/interfaces in
  `AgentReplyContracts.cs`:

  ```csharp
  public sealed record AgentReplyPresentation(
      string VisibleText,
      IReadOnlyList<string> VtsCodes,
      string? Emotion);

  public interface IAgentReplyPolicy
  {
      ValueTask<AgentReplyPresentation> PrepareAsync(
          AgentRun run,
          string rawText,
          CancellationToken cancellationToken);
  }

  public interface IAgentReplyEffectSink
  {
      ValueTask ApplyAsync(
          AgentRun run,
          AgentReplyPresentation reply,
          ModelStepId modelStepId,
          CancellationToken cancellationToken);
  }
  ```

  `VisibleText` must be non-empty and at most the existing AssistantText payload cap;
  each VTS code is lower-case and at most 128 characters; the code list is capped at
  20; `Emotion` is one of `none`, `joy`, `sad`, `angry`, `surprised`, `shy` or null.
  Invalid values throw `ArgumentException` at the contract boundary.
- [ ] Add `PassthroughAgentReplyPolicy` and
  `NoopAgentReplyEffectSink` in the Core project for unit tests and hosts that do not
  register backend effects. The passthrough policy returns the input text and empty
  metadata; it does not alter the existing Core test default.

### 2.2 Sanitize the final Agent payload before committing it

- [ ] Add optional `IAgentReplyPolicy? replyPolicy` and
  `IAgentReplyEffectSink? replyEffects` parameters to the end of the
  `AgentRunCoordinator` constructor, preserving all existing call sites through the
  defaults.
- [ ] In `HandleFinalStepAsync`, call
  `PrepareAsync(run, step.Text, cancellationToken)` before allocating the
  `AssistantText` payload. Reject an empty prepared `VisibleText` with the existing
  `model_final_text_missing` error. Encode `reply.VisibleText`, never `step.Text`, in
  the authenticated/model payload and in the assistant conversation outbox.
- [ ] After the durable `CommitAsync` succeeds, call `replyEffects.ApplyAsync` with
  `CancellationToken.None` inside a catch block that swallows only non-fatal effect
  exceptions and emits the bounded safe code `reply_effect_failed` through the
  existing activity sink. `OperationCanceledException` from the model path remains
  unchanged; a post-commit VTS failure must not roll back or fail the run.
- [ ] Keep transient model-step/activity ordering unchanged. The final reply policy
  must not publish raw metadata through an audit event or payload.
- [ ] Add Core tests proving (a) prepared visible text is what the payload/outbox
  contains, (b) a policy exception fails before a final commit, (c) an effect exception
  does not change a committed terminal transition, and (d) the default passthrough
  keeps existing coordinator tests green.

### 2.3 Port incremental private-marker sanitization to Agent deltas

- [ ] Move the behavior of `StreamSanitizer.Append`/`Visible` into the new
  `AgentReplyTransientRedactor` without changing its marker vocabulary:
  `[[VTS:...]]`, `[[VOICE_PACE:...]]`, `[[EMO:...]]`, `[SEARCH:...]`,
  `[APP_OPEN:...]`, and `[VOLUME:...]`.
- [ ] Compose the existing `KnownSecretRedactor` session with the marker session so
  secrets are still redacted before a transient frame is emitted. The session holds a
  raw/visible prefix, emits only the newly visible suffix, holds a split marker until
  the next delta, and flushes the final visible suffix from `Complete()`.
- [ ] Pass this composed redactor to the Agent coordinator factory for model deltas;
  tool-output deltas continue to use the existing secret redaction rules. The final
  reply policy remains the authoritative full-text sanitizer when a stream is absent
  or a marker closes at terminal time.
- [ ] Add tests for a marker split across two deltas, a marker at the first delta, an
  unterminated marker, a secret split across deltas, normal UTF-8 text, and the
  prefix-only invariant. A frame must never contain a private marker or secret.

### 2.4 Implement the backend reply policy and VTS effect sink

- [ ] Add a small `IAgentVtsClient` adapter in
  `backend-dotnet/RuBackend/Harness/AgentReplyPolicy.cs` over the current loopback
  VTS catalog/action methods. The adapter exposes only `GetCatalogAsync(requestId)`
  and `TriggerAsync(requestId, codes)`; it does not expose Chat or memory methods.
- [ ] Implement `AgentReplyPolicy` by fetching a catalog with a bounded request id,
  calling the existing `MetadataPolicy.SplitReply(rawText, catalog)`, then
  `StripVoiceMarkers` on the split visible text. If catalog retrieval is unavailable,
  still return sanitized visible text, emotion, and an empty code list; never expose
  an unvalidated code.
- [ ] Implement `AgentReplyEffectSink` with a concurrent set keyed by
  `(run.RunId, modelStepId)` so reconnect/retry cannot trigger the same VTS action
  twice in one process. It calls `TriggerAsync` only for non-empty validated codes,
  appends a safe `agent` activity event containing role, session id, visible content,
  and optional emotion using a deterministic event id, and records `reply_effect_failed`
  when either non-critical operation fails.
- [ ] Move `MetadataPolicy` and `StreamSanitizer` implementation into the Agent
  namespace/files after parity tests pass, preserving all existing regexes and
  Vietnamese emotion heuristics byte-for-byte. Update namespaces/usings rather than
  leaving a misleading `Conversation` production module.
- [ ] Register the policy, effect sink, VTS adapter, and composed transient redactor in
  `Program.cs`; pass them through `AgentRuntimeCoordinatorFactory` into every
  `AgentRunCoordinator`. The factory must not create a second generation service.

### 2.5 Preserve memory retrieval, learning, and user activity

- [ ] Add `AgentMemoryContextProvider` in `AgentMemoryProjection.cs`. It receives
  `IMemoryService` and `ISettingsStore`, uses the existing `ShouldShareMemory` rule
  (`Backend == "llama.cpp"` or `ShareMemoryWithCloud`), retrieves for the latest
  user prompt, renders `kind: content` lines with the existing 100,000-character
  bound, and returns null when sharing is disabled or no records match.
- [ ] Change `HarnessModelContextBootstrapper` to receive this provider. During the
  first user outbox capture, append one `ModelContextRole.System` base entry with
  source id `base:memory` and content `Relevant memory:\n` plus the bounded rendering.
  Keep historical user/assistant entries and their sequence order unchanged; append
  the memory entry after them so `LoopbackModelStepRunner.ExtractTurns` continues to
  pair historical turns correctly. A missing memory store is treated as an empty
  context, not as a failed user turn.
- [ ] Add `AgentConversationMemoryProjector` with a durable in-process dedup key of
  the assistant outbox `IntentId`. After `AppendIfAbsentAsync` succeeds for an
  assistant claim, load the newest complete session turn and, when
  `EnableSelfLearning` is true, call `RememberTurnAsync` and `EnqueueLearning`. Memory
  failures are logged as bounded `memory_projection_failed` and do not fail history or
  the run. Replayed candidates remain idempotent through MemoryService upsert keys.
- [ ] Inject the projector into `AgentConversationOutboxDispatcher`; execute it only
  for assistant claims and before the outbox acknowledgement. User claims still only
  capture model context. Add deterministic tests for enabled/disabled learning,
  sharing-disabled retrieval, duplicate intent dispatch, memory-store failure, and
  session-not-found handling.
- [ ] Add a safe user activity projection keyed by the user outbox intent and change
  the old `type: "chat"` activity payload to `type: "agent"`. Activity data contains
  only role, session id, visible content, and bounded emotion; it never includes raw
  model metadata, tool arguments, credentials, or workspace paths. Update the activity
  preview tests accordingly.

### 2.6 Run the focused green gates and commit

- [ ] Run sequentially:

  ```powershell
  dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentReply|FullyQualifiedName~AgentMemory|FullyQualifiedName~MetadataPolicy|FullyQualifiedName~StreamSanitizer"
  ```

  Expected result: all selected tests pass and no `/chat` deletion has happened yet.
- [ ] Inspect `git diff --check` and stage only the Core/backend files from this task.
  Commit with `refactor: move reply and memory behavior into Agent path`.

## Task 3: Add typed Agent assistant-delta delivery to the client

**Files**

* Add `backend-dotnet/RuAgent.Client/AgentAssistantDeltaModel.cs`.
* Add `backend-dotnet/RuAgent.Client.Tests/AgentAssistantDeltaModelTests.cs`.
* Modify `gui-winui3/Services/IAgentRunClient.cs`.
* Modify `gui-winui3/Services/AgentRunClientAdapter.cs`.
* Modify `gui-winui3/Services/AgentRunController.cs`.
* Extend `gui-winui3.Tests/AgentRunClientAdapterTests.cs` and
  `gui-winui3.Tests/AgentRunControllerTests.cs`.

### 3.1 Define and validate the transient payload

- [ ] Add `AgentAssistantDeltaModel` with `Text`, `ModelStepId`, `ExecutionId?`,
  and `TransientSequence`. Its parser accepts only `frame.IsTransient == true`,
  `frame.EventType == "assistant.delta"`, a JSON object with `text` (1..64 KiB),
  `model_step_id` (a valid Agent model-step id), and optional `execution_id`; unknown
  fields and malformed JSON return false. The parser must not expose arbitrary JSON or
  raw tool data to the UI.
- [ ] Unit-test valid UTF-8, missing/unknown fields, empty/over-limit text, wrong
  event type, non-transient frames, invalid model-step ids, and missing transient ids.

### 3.2 Thread the callback through adapter and controller

- [ ] Add an optional `Func<AgentAssistantDeltaModel, Task>? onAssistantDelta` parameter
  to `IAgentRunClient.SubscribeEventsAsync` after the existing summary callback, and
  parse/dispatch it before activity parsing. Keep the existing behavior that malformed
  transient frames and raw `tool.output.delta` are dropped.
- [ ] Add `AssistantDeltaReceived` to `AgentRunController`. It must run on the same UI
  dispatcher as activity/summary, ignore callbacks when the controller is not busy or
  cancellation was requested, and swallow only subscriber exceptions.
- [ ] Deduplicate assistant deltas by `(runId, modelStepId, transientSequence)` and
  reset the set for each new run. A reconnect that replays the same transient frame
  cannot append duplicate visible text; durable events still use their normal cursor.
- [ ] Add adapter/controller tests proving typed delivery, dispatcher use, raw-tool
  dropping, malformed-frame dropping, cancellation filtering, and duplicate
  suppression.

### 3.3 Verify and commit

- [ ] Run:

  ```powershell
  dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentRunClientAdapter|FullyQualifiedName~AgentRunController"
  ```

- [ ] Inspect docs for stale claims about “raw assistant deltas never reaching the
  UI”; update `docs/operations/runtime-behavior.md` to describe typed
  `assistant.delta` delivery. Commit explicit client/test/doc paths with
  `feat: expose typed Agent assistant deltas`.

## Task 4: Route text and microphone turns through the same Agent run

Do this before deleting the Chat client. The acceptance invariant is that a text turn
and a transcribed microphone turn call `CreateRunAsync` and never call
`ChatWebSocketClient`.

**Files**

* Modify `gui-winui3/Views/ChatView.xaml.cs`.
* Modify `gui-winui3/Services/VoiceConversationService.cs` only for callback naming
  and cancellation documentation; keep capture/transcription boundaries unchanged.
* Extend `gui-winui3.Tests/ChatViewTurnRoutingContractTests.cs`.
* Extend `gui-winui3.Tests/ChatViewVoiceContractTests.cs` and
  `gui-winui3.Tests/VoiceConversationServiceTests.cs`.

### 4.1 Consolidate turn submission

- [ ] Refactor the existing text method into one
  `SendAgentTurnAsync(string text, AgentTurnSource source, CancellationToken)`
  helper. `AgentTurnSource` is an internal enum with `Typed` and `Voice`; it only
  controls presentation/playback and never changes the backend route.
- [ ] Keep the current workspace resolution, session creation/recovery, preset
  versioning, trust/approval handling, user bubble, and terminal history refresh in
  this helper. There is no fallback branch when Agent creation fails; surface the
  typed Agent error copy already used by `HandleSendError`.
- [ ] Register `AgentRunController.AssistantDeltaReceived` once in the view. For the
  active run, append each typed delta to the current assistant message using the
  existing prefix/sequence safeguards; scroll only the Chat viewport, not the whole
  shell. Ignore deltas for an old run after session replacement.
- [ ] On `AgentRunController` terminal completion, reload the durable session history.
  If a voice run received no delta, create/complete exactly one assistant bubble from
  the persisted final payload; if deltas were received, do not append the same final
  text a second time.

### 4.2 Integrate voice playback without delaying Agent completion

- [ ] Change `SendVoiceMessageAsync` to call `SendAgentTurnAsync(..., Voice, ...)`
  after `/voice/transcribe`; remove its call to `SendMessageTextAsync`.
- [ ] For a voice source, call `VoicePlaybackService.BeginReplyStream` when the Agent
  assistant bubble is created, call `AppendReplyDelta` for each typed delta, and call
  the existing completion method once at terminal/fallback. `MediaPlayer.Play()` must
  remain before the non-blocking capacity-1 subtitle notification, so subtitles never
  delay audio.
- [ ] Remove `_voiceTurnPending` markers that only coordinated Chat events. Replace
  them with an active Agent run id and a completion guard keyed by run id; canceling or
  replacing the session clears that guard and stops playback through the existing
  voice service.
- [ ] Delete the `_agentMode` microphone-off guard in `OnMicClick`; microphone capture
  is enabled in the only remaining Agent mode. Preserve the explicit permission/error
  copy and half-duplex state machine.
- [ ] Remove `SendMessageTextAsync`, `_chatTurnQueue`, `_chatTurnPresenter`,
  `OnWsFrameReceived`, `ApplyChatTurnUpdateAsync`, Chat reset/done/error handlers, and
  Chat-only streaming error helpers only after the tests in this task prove the Agent
  path covers their user-visible cases.

### 4.3 Add routing/playback regression tests

- [ ] Assert the view’s send path contains `CreateRunAsync`/Agent SSE and has no
  `SendMessageAsync`, `ChatWebSocketClient`, or `/chat` reference.
- [ ] Assert typed voice transcription calls the same Agent send helper as typed text,
  starts playback, appends deltas in order, completes once, and does not duplicate the
  persisted final assistant text.
- [ ] Assert Agent mode does not disable `MicButton`, old-run deltas are ignored, and
  cancellation stops the active Agent subscription/playback without a Chat error.
- [ ] Run the focused WinUI tests and commit with
  `refactor: send text and voice through Agent`.

## Task 5: Remove the public Chat WebSocket and Chat coordinator module

**Files to delete after Task 4 is green**

* `backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs`
* `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs`
* `backend-dotnet/RuBackend/Conversation/ChatRequest.cs`
* `backend-dotnet/RuBackend/Conversation/ChatEvent.cs`
* `backend-dotnet/RuBackend/Conversation/ChatCorrelation.cs`
* Chat-only frame/queue/gate files in the same directory, including
  `ConversationTurnGate.cs`, `ConversationSessionGate.cs`,
  `ConversationIntentResolver.cs`, `ConversationIntentResolution.cs`, and
  `ActionOutcomeContextStore.cs`, once `rg` confirms no non-Chat caller.
* `backend-dotnet/RuBackend/Contracts/ChatFrame.cs` if no protocol or test caller
  remains.
* Chat-only fixtures/tests after replacement contract tests are added.

**Files to modify**

* `backend-dotnet/RuBackend/Program.cs`.
* `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs` if its session-delete
  action-outcome dependency is now unused.
* `backend-dotnet/RuBackend.Tests/PublicRouteContractTests.cs`.
* Add `backend-dotnet/RuBackend.Tests/PublicRouteChatRemovalTests.cs`.

### 5.1 Remove registration and mapping

- [ ] Delete the Chat DI registrations from `Program.cs`: conversation gates,
  intent resolver, `IConversationCoordinator`, Chat metadata/sanitizer registrations
  after the Agent equivalents are registered, and `IActionOutcomeContextStore` if
  `rg` proves it was Chat-only. Keep memory, VTS, voice, Agent Harness, trust, and
  approval registrations.
- [ ] Delete `ChatWebSocketEndpoint.Map(app)` and leave
  `VoiceStreamEndpoint.Map(app)` plus all HTTP/Agent mappings in their existing order.
- [ ] Remove the `ChatWebSocketEndpoint` source entry and `/chat` object from
  `backend-dotnet/public-route-contract.json`. Do not add a redirect or OPTIONS
  special case; an upgrade/request to `/chat` must be a normal unknown path.
- [ ] Remove `ChatWebSocketEndpoint`-specific source checks from route tests and add a
  host-level test that an authenticated request to `/chat` returns 404/upgrade close
  without invoking any model or Worker handler.

### 5.2 Delete Chat-only backend tests and preserve Agent coverage

- [ ] Delete `ConversationCoordinatorTests.cs`, `ChatWebSocketEndpointTests.cs`,
  `ChatCorrelationTests.cs`, `ChatContractFixtureTests.cs`,
  `ConversationSessionGateTests.cs`, `ConversationIntentResolverTests.cs`, and
  `ActionOutcomeContextStoreTests.cs` only when `rg` shows no remaining production
  dependency; move any behavior assertion that still applies to Agent into the new
  `AgentReply*`, `AgentMemory*`, `AgentV1*`, or `AgentRunCoordinator` tests.
- [ ] Delete `tests/fixtures/chat-orchestration.json` after its useful assertions are
  represented in Agent contract fixtures. Keep all memory/VTS/action outcome tests
  that exercise a surviving Agent or device path.
- [ ] Run `rg -n "ChatWebSocket|IConversationCoordinator|ConversationCoordinator|/chat|chat\." backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests`.
  At this checkpoint, matches are allowed only in explicit negative route tests and
  migration history/docs not yet updated; no production registration, mapping, or
  call site may remain.

### 5.3 Verify the host deletion and commit

- [ ] Run sequentially:

  ```powershell
  dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~PublicRoute|FullyQualifiedName~AgentReply|FullyQualifiedName~AgentMemory|FullyQualifiedName~AgentV1"
  & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_aspnetcore_route_matrix.py tests/test_agent_only_contract.py -q
  ```

- [ ] Confirm the route checker now reports `54 HTTP + 1 WebSocket` and commit explicit
  backend/test/contract paths with `refactor: remove Chat public host module`.

## Task 6: Retire Worker v1 Chat protocol and split the Worker adapter seam

Task 6 removes only conversation generation from Worker v1. Runtime load/request,
voice/STT, VTS, observation, health, and the Worker v2 Agent step protocol remain.

**C# files**

* Modify `backend-dotnet/RuBackend/Worker/IWorkerClient.cs`.
* Modify `backend-dotnet/RuBackend/Worker/WorkerModels.cs`.
* Modify `backend-dotnet/RuBackend/Worker/WorkerMessageTypes.cs`.
* Modify `backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs`.
* Modify `backend-dotnet/RuBackend/Worker/WorkerConnection.cs`.
* Modify `backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs`.
* Modify/remove `backend-dotnet/RuBackend/Worker/WorkerActionOutcome.cs` after caller
  inventory.
* Split `backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs` into
  surviving runtime/voice/VTS tests and delete Chat-only cases.
* Update `WorkerEnvelopeTests.cs`, `WorkerV2ContractTests.cs`, and
  `WorkerV2ClientTests.cs` to retain v2 rejection of an invalid legacy tool frame
  only where that rejection is still a deliberate protocol-security assertion.

**Python files**

* Modify `src/ru_runtime/worker/v1/protocol.py`.
* Modify `src/ru_runtime/worker/v1/server.py`.
* Modify `src/ru_runtime/worker/v1/handlers.py`.
* Modify `src/ru_runtime/worker/v1/runtime_services.py` only for imports/locks that
  become unused.
* Move the shared `ChatTurn` value object to
  `src/ru_runtime/generation/turns.py` as `ConversationTurn`; update
  `context.py`, `orchestration.py`, `runner.py`, and
  `worker/v2/model_step.py`, then delete `generation/chat.py`. This keeps v2’s
  internal prompt-history pairing without preserving a public Chat concept.
* Update/delete Chat-only tests in `tests/test_worker_server.py`,
  `tests/test_worker_protocol.py`, `tests/test_worker_runtime_handlers.py`, and
  `tests/test_agent_harness.py`; keep runtime/voice/VTS/observation tests.

### 6.1 Remove the C# Chat surface

- [x] Remove `WorkerChatRequest`, `WorkerTurn`, Chat tool request/result/cancel records,
  `GenerateChatAsync`, `ChatStart/Reset/Chunk/Done/Cancel/Tool*`, and Chat-only
  `ChatDoneFields` validation. Keep `ConnectAsync`, health, runtime, voice, VTS, and
  the interfaces consumed by `LoopbackRuntimeWorkerClient`/`VoiceStreamEndpoint`.
- [x] Remove `MemoryExtract`/`ExtractFactsAsync` only after `rg -n
  "ExtractFactsAsync|memory\.extract|MemoryFacts" backend-dotnet src tests` proves no
  surviving caller. Memory learning now belongs to `AgentMemoryProjection`.
- [x] If `IWorkerClient` still mixes unrelated capabilities after deletion, split it
  into `IWorkerConnectionClient`, `IVtsProtocolClient`, and the already existing
  `IRuntimeProtocolClient`/`IVoiceProtocolClient`; register the smallest interface at
  each injection point. `LoopbackWorkerClient` may implement multiple interfaces, but
  no interface may contain a Chat-named method.
- [x] Remove Chat capability advertisement and validation from `WorkerConnection`;
  preserve `runtime`, `voice`, `vts`, `observation`, and health capabilities. A v1
  `chat.start` request now receives `unsupported_operation` from the generic unknown
  operation path only if protocol validation accepts it; the preferred behavior is
  `unknown_type` after the constant is removed. Add one negative test and document the
  chosen code as the stable retirement response.

### 6.2 Remove the Python v1 Chat branch

- [x] Delete `CHAT_START`, `CHAT_RESET`, `CHAT_CHUNK`, `CHAT_DONE`, `CHAT_CANCEL`,
  `CHAT_TOOL_REQUEST`, `CHAT_TOOL_RESULT`, `CHAT_TOOL_CANCEL`, `MEMORY_EXTRACT`, and
  `MEMORY_FACTS` from `protocol.py`’s known/ordered sets and validators. Keep
  runtime/voice/VTS/health constants and action-outcome validation used by surviving
  device endpoints.
- [x] Change `WorkerHandlers` to contain only `runtime`, `voice`, and `vts` handlers.
  Remove the Chat tool-result queues, Chat cancellation branch, and Chat-specific
  session/correlation plumbing from `server.py`; keep generic request cancellation
  for runtime/voice/VTS operations.
- [x] Remove `_chat_handler`, `_parse_chat_request`, Chat action selection/follow-up
  code, and `memory_extract` handler construction from `handlers.py`. Retain the
  runtime handler’s generation helper where observation/device behavior still calls
  `runner.generate_reply`.
- [x] Change the hello capability list to omit `chat` and `memory.extract`; retain
  `runtime`, `voice`, `vts`, and `observation`. Update the Python protocol fixture to
  assert the exact surviving set.
- [x] Update `serve_runtime_worker` shutdown imports only if `generation.search` is no
  longer used by a surviving runtime path; otherwise retain it. Do not delete search
  merely because Chat was deleted.

### 6.3 Rename the shared internal turn type and test

- [x] Add `ConversationTurn` to `src/ru_runtime/generation/turns.py` with the exact
  two fields/validation previously provided by `ChatTurn`; update all four generation
  consumers and type annotations.
- [x] Move direct `ChatTurn` tests to `tests/test_generation_turns.py` and update
  `tests/test_worker_v2_step.py`, `tests/test_worker_v2_server.py`, and
  `tests/test_agent_harness.py` to drive Worker v2 Agent step messages rather than
  v1 `chat.done` fixtures.
- [x] Run sequentially:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_worker_protocol.py tests/test_worker_server.py tests/test_worker_runtime_handlers.py tests/test_worker_v2_protocol.py tests/test_worker_v2_step.py tests/test_worker_v2_server.py tests/test_agent_harness.py -q
  dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~Worker"
  ```

- [x] Run `rg -n "GenerateChatAsync|WorkerChatRequest|CHAT_START|CHAT_DONE|MEMORY_EXTRACT|chat\.start|chat\.done" backend-dotnet/RuBackend src/ru_runtime/worker/v1` and require zero production matches. Keep explicit negative tests in test files only if they assert the retirement code.
- [x] Commit explicit Worker/Python/test paths with `refactor: retire Worker v1 Chat protocol`.

## Task 7: Remove the GUI Chat transport and mode branch

**Files**

* Modify `gui-winui3/MainWindow.xaml.cs`.
* Modify `gui-winui3/Services/BackendSession.cs`.
* Modify `gui-winui3/Views/ChatView.xaml.cs`.
* Modify `gui-winui3/Views/ChatView.xaml`.
* Modify composition/bootstrap files that inject `ChatWebSocketClient`.
* Delete `gui-winui3/Services/ChatWebSocketClient.cs`,
  `ChatEventFrame.cs`, `ChatTurnQueue.cs`, `ChatTurnPresenter.cs`, and `ChatTurn.cs`
  only after `rg` shows no surviving production caller.
* Delete/update `gui-winui3.Tests/ChatWebSocketClientContractTests.cs`,
  `ChatTurnQueueTests.cs`, `ChatTurnPresenterTests.cs`, and mode-specific assertions.

### 7.1 Remove socket construction and route state

- [x] Remove the `_wsClient` field, `new ChatWebSocketClient(backendSession)`,
  `Start/NotifyBackendStopped/Dispose`, event subscriptions, and constructor argument
  from `MainWindow.xaml.cs` and `ChatView` composition. Keep the Agent client/controller
  and VoiceConversationService lifetime/disposal.
- [x] Remove `BackendSession.ChatUri`; keep `BaseUri`, bearer, `/voice/stream`, and
  Agent HTTP construction. `rg -n "ChatUri|/chat|ChatWebSocketClient" gui-winui3` must
  show only negative tests/docs until final cleanup.
- [x] Remove mode-specific `_agentMode` branches from `ChatView.xaml.cs`. The view
  starts in Agent mode, `SetAgentMode`/Chat option handlers disappear, and all sends
  call the consolidated Agent helper from Task 4.
- [x] Keep the existing `ChatView` class/automation IDs in this cut to avoid a broad
  visual rename. Remove the mode flyout/radio buttons from `ChatView.xaml`; show a
  static compact `Agent` label and retain only `AgentPresetBox` with `Standard` and
  `Full Access`. No workspace editor/settings flyout is reintroduced.
- [x] Make the Agent preset selector visible whenever the composer is visible, with
  Standard selected by default. Preserve Full Access confirmation, session preset
  versioning, and the current workspace-resolution policy; this task does not broaden
  filesystem authority.
- [x] Keep the existing centered transcript and `ChatScrollHost` right-edge scrollbar
  layout. Remove only code that was required to stream Chat WebSocket frames.

### 7.2 Update UI contract tests and build

- [x] Replace mode-dropdown assertions in `ChatShellUiContractTests.cs` with exact
  assertions for one Agent label, one preset ComboBox, two preset items, no Chat radio,
  no workspace editor in the composer, and an enabled microphone button.
- [x] Replace `ChatWebSocketClientContractTests` with a negative static contract test
  that the production project has no Chat WebSocket type/URI and a positive test that
  the Agent client is composed.
- [x] Update `ChatViewNoEmojiContractTests`, scroll tests, and welcome stability tests
  only where their fixture names refer to the removed mode; preserve their behavioral
  assertions.
- [x] Run:

  ```powershell
  dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
  dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
  ```

- [x] Commit explicit GUI/test paths with `refactor: make Agent the only GUI mode`.

## Task 8: Update route tools, docs, examples, and operator contract

**Files to inspect/update**

* `backend-dotnet/public-route-contract.json` (already changed in Task 5; verify).
* `scripts/check_aspnetcore_route_matrix.py` (already changed in Task 1; verify).
* `scripts/benchmark_aspnetcore_compat.py`.
* `scripts/harness/session.py`.
* `tests/test_harness_session.py`, `tests/test_harness_routes.py`, and
  `tests/test_aspnetcore_compatibility_tools.py`.
* `README.md`, `backend-dotnet/README.md`, `docs/README.md`.
* `docs/architecture.md`, `docs/operations/runtime-behavior.md`,
  `docs/operations/agent-tools.md`, and `docs/operations/verification.md`.
* `CONTEXT.md` and `docs/adr/0007-mode-capability-profiles.md`.
* `docs/superpowers/specs/2026-08-29-ru-agent-only-chat-removal-design.md` only
  when an implementation detail changed from the approved contract; do not rewrite
  the approved decisions to hide a deviation.

### 8.1 Make all tooling Agent-only

- [x] Remove `("/chat", ...)` from the compatibility benchmark and retain the
  `/voice/stream` probe plus all 54 HTTP probes.
- [x] Remove `_websocket_url("/chat", ...)`, Chat send helpers, and Chat route fixtures
  from `scripts/harness/session.py` and its tests. Keep the private Worker v1 URL only
  for device/runtime probes and the Worker v2 URL for Agent model steps.
- [x] Update every route count/summary from `54 HTTP + 2 WebSocket` to
  `54 HTTP + 1 WebSocket`; list `/voice/stream` as the only public WebSocket.
- [x] Update examples so text and voice both show `POST /agent/v1/runs` plus SSE
  `GET /agent/v1/runs/{runId}/events`; remove all `/chat` send examples and
  `--deterministic-chat` aliases. If a CLI flag must remain for parser compatibility,
  rename its help text to Agent-only and make the old token return a clear
  `unsupported_operation` error rather than starting Chat.

### 8.2 Rewrite architecture/operations/ADR language

- [x] In `docs/architecture.md`, change the ownership table from “v1 Chat + v2
  Agent” to “v1 device/runtime + v2 Agent”, describe the single Agent text/voice
  path, remove the `/chat` endpoint and Chat coordinator module, and retain the
  explicit note that prompt-composer parity is a follow-up.
- [x] In `docs/operations/runtime-behavior.md`, replace Chat turn ordering/queue
  paragraphs with Agent run/SSE ordering, document typed assistant deltas and voice
  transcription→Agent flow, and keep metadata privacy/TTS/VTS/memory/approval and
  right-edge scrollbar behavior accurate.
- [x] In `docs/operations/agent-tools.md`, remove the Chat/Agent capability matrix;
  document Agent Standard and Full Access only, with session-scoped workspace policy,
  approval, trust, cancel, reconnect, and external-directory behavior. Keep the
  future OS sandbox deferral.
- [x] In `CONTEXT.md` and ADR 0007, change the default/only generation mode to Agent,
  remove the Chat read-only profile, keep Full Access as the explicit loosening of
  policy, and state that no Chat compatibility shim exists.
- [x] Update root/backend/docs READMEs and verification prose so commands, route
  counts, startup descriptions, and screenshots cannot imply a Chat transport.
- [x] Add a short “retirement boundary” paragraph to the approved spec noting the
  implementation date and the preserved non-conversation Worker v1 responsibilities.

### 8.3 Run documentation and static-contract checks

- [x] Run `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py tests/test_legacy_retirement.py tests/test_aspnetcore_route_matrix.py tests/test_harness_session.py tests/test_harness_routes.py -q`.
- [x] Run `rg -n -i "54 HTTP.*2 WebSocket|/chat|ChatWebSocket|GenerateChatAsync|Worker v1 Chat|normal Chat|Chat mode|starts in Chat" README.md backend-dotnet docs CONTEXT.md scripts tests -g "*.md" -g "*.py" -g "*.json" -g "*.cs" -g "*.xaml"`. Remaining matches must be either the explicit negative retirement test or historical/spec text that is clearly marked superseded; fix every active-operator claim.
- [x] Inspect all changed docs for outdated links and commit explicit docs/tool paths
  with `docs: document Agent-only runtime contract`.

## Task 9: Full sequential verification and user-owned acceptance

### 9.1 Deterministic release gates

- [x] Restore every .NET project listed in `docs/operations/verification.md` one at a
  time, then run the Release test/build matrix sequentially. Use the repository’s
  exact commands, including:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
  & D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
  & D:\LLM\.venv\Scripts\python.exe -m pytest -q
  & D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
  & D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output "$env:TEMP\\ru-agent-only-harness.json"
  & D:\LLM\.venv\Scripts\python.exe -m pip check
  dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
  dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
  dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
  cmd /c D:\LLM\build.bat
  ```

- [x] Require the route checker output to say exactly `54 HTTP + 1 WebSocket`, the
  Python suite and all .NET/WinUI projects to exit 0, `git diff --check` to be clean,
  and the Release artifact to be
  `D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`.
- [x] Run a final production/static inventory:
  `rg -n "ChatWebSocket|IConversationCoordinator|GenerateChatAsync|WorkerChatRequest|CHAT_START|CHAT_DONE|/chat|ChatUri|_agentMode" backend-dotnet/RuBackend gui-winui3 src/ru_runtime/worker/v1 scripts`.
  Require zero production matches; allow only explicitly named negative tests outside
  production code.
- [x] Inspect `git status --short` and every diff. Confirm no unrelated dirty file was
  staged or changed. Keep the approved spec, plan, and implementation commits separate
  from pre-existing user edits.

### 9.2 Explicit manual acceptance (not claimed by deterministic tests)

- [ ] Launch the freshly published `RuApp.exe` through the user’s updated shortcut.
  The composer shows Agent and Standard/Full Access only; no Chat option or workspace
  editor appears.
- [ ] Send a typed prompt, approve/trust a tool when requested, cancel one run, and
  continue one waiting run. Confirm activity cards and assistant deltas appear without
  raw tool output or private markers.
- [ ] Capture a microphone turn in Agent mode. Confirm transcription creates an Agent
  run, visible deltas stream into the same assistant bubble, playback starts before
  subtitle notifications, and the final answer is not duplicated.
- [ ] Grow the transcript until scrolling is required. Confirm the reading column stays
  centered, the vertical scrollbar hugs the far-right edge of the Chat viewport, and
  horizontal scrolling remains disabled.
- [ ] Separately test live speaker/microphone/VTube Studio behavior. Deterministic
  tests do not prove audible quality, physical microphone permissions, or VTS model
  connectivity.

### 9.3 Completion report

- [x] Report the exact commit hashes, route count, deterministic suite summaries,
  remaining manual checks, and any intentionally retained historical/negative Chat
  strings. Do not report “fixed” or “complete” without the release matrix and final
  inventory evidence.
