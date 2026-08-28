# Ru Agent-only Backend: Remove Chat and Move Voice Through Agent

## Status

Draft for specification review. The product direction was approved in the
preceding conversation; this document narrows the implementation to one
focused backend cut.

## Date

2026-08-29

## Decision summary

Ru will have one conversation runtime: Agent. The Public Host will expose
`/agent/v1/*` as the only text-generation/orchestration contract. The `/chat`
WebSocket and its complete Chat orchestration path will be deleted, not
bridged, redirected, or feature-flagged.

Voice remains a device capability rather than a second conversation runtime.
Microphone input is transcribed through `/voice/transcribe`, submitted as the
text of an Agent run, and rendered from Agent SSE assistant deltas. The
existing `/voice/stream`, Voice Engine, VTube Studio, observation, and other
non-conversation runtime contracts remain available. They do not recreate Chat
semantics.

This cut intentionally does not implement the OpenCode-style system-prompt
composer. That work is a separate follow-up project after the single Agent
path is green. Keeping it separate reduces backend depth risk, keeps the
deletion reviewable, and avoids changing prompt semantics while transport
ownership is being consolidated.

## Why this is the clean cut

The current backend has two generation paths that duplicate session ordering,
history, memory, metadata filtering, cancellation, and error mapping:

```text
Current
RuApp -> /chat -> ChatWebSocketEndpoint -> ConversationCoordinator -> Worker v1 Chat
RuApp -> /agent/v1/* -> AgentV1Service -> Harness Core -> Worker v2 Agent
```

Agent already owns the deeper execution behavior: idempotency, durable run
state, Broker policy, trust and approval ordering, SSE replay, cancellation,
and reconciliation. Keeping Chat for compatibility would retain a shallow
pass-through seam and two public contracts. The deletion test therefore says
to remove Chat rather than adapt it.

The clean target is:

```text
Target
RuApp / RuAgent.Cli -> /agent/v1/* -> AgentV1Service/Harness Core -> Worker v2
                                      |-> Broker / history / memory
                                      |-> Runtime device capability adapters
RuApp microphone -> /voice/transcribe -> same Agent run -> Agent SSE -> /voice/stream + VTS
```

## Goals

1. Make Agent the only text-generation and conversation orchestration path.
2. Delete `/chat` and all Chat-only backend protocol, state, registrations,
   tests, fixtures, and documentation claims.
3. Route microphone transcripts through the existing Agent run/SSE lifecycle
   without creating duplicate history or a second queue.
4. Preserve memory retrieval/learning, metadata sanitization, VTS actions,
   settings, history, approval, trust, cancellation, and reconnect behavior.
5. Keep voice/STT/TTS/VTS/observation device capabilities independently usable
   after Chat generation is gone.
6. Reduce the backend's public contract to 54 HTTP method/path pairs and one
   WebSocket route: `/voice/stream`.
7. Keep the change maintainable: one deep Agent execution path, small
   capability seams, and no new conversation façade that merely forwards to
   `AgentRunController` or `AgentV1Service`.

## Non-goals

- No compatibility bridge for `/chat`. Old clients must migrate to Agent.
- No OpenCode-style prompt composer, prompt snapshot protocol, or system
  message reordering in this cut. Those are specified separately after this
  migration.
- No replacement of the Runtime Worker transport used by non-conversation
  voice, STT, VTS, observation, status, or settings endpoints.
- No OS-enforced filesystem/network sandbox. Existing Broker policy remains
  intent-level for terminal networking, as recorded by ADR 0007; the future
  sandbox is a separate project.
- No UI visual redesign. The existing conversation view may keep its
  `ChatView` class/file name temporarily, but it must no longer construct or
  call a Chat WebSocket.

## Architecture and ownership

### Public Host

ASP.NET Core remains the only public owner. `AgentV1Endpoints` and
`AgentV1Service` own creation, state transitions, approvals, trust, continue,
cancellation, SSE replay, payload access, and reconciliation. The Host does
not expose a second text-generation route.

### Agent execution

Harness Core remains the execution implementation behind the Agent seam. It
owns durable run state, one-step model driving, tool receipts, Broker lease
binding, outbox/history transitions, and safe live events. Agent SSE is the
single source of transient assistant deltas and durable terminal summaries.

### Runtime device capabilities

The Runtime Worker remains an implementation of device/runtime capabilities
that are not Chat generation: STT, Voice Engine/TTS streaming, VTS catalog and
actions, observation, health, status, and related settings calls. Its loopback
connection may remain shared, but the Chat request type and Chat generation
branch are removed. If the shared adapter is still too broad after deletion,
split it by capability at an internal seam without changing the public
contract.

### Client and UI

The current Agent client/controller is the conversation implementation for
both typed and voice turns. Do not add a second client abstraction solely to
rename Chat. The voice controller submits a transcript through the same Agent
run creation and event subscription path as typed input. `ChatView` can remain
the visual host until a later naming-only cleanup; its production dependencies
must be Agent and device capability clients only.

## Required behavior

### Typed turn

1. The user submits text.
2. The client creates one idempotent Agent run with the selected preset and
   optional run root.
3. The Host opens/reuses the existing Broker lease and drives Worker v2.
4. Agent SSE emits safe activity/approval events and ordered
   `assistant.delta` frames.
5. The UI renders visible assistant text and reconciles durable history at the
   terminal boundary.

There is no local Chat fast path, Chat queue, Chat-specific session gate, or
Worker v1 generation fallback after the cut.

### Voice turn

1. The microphone captures one bounded 16 kHz mono turn under the existing
   explicit half-duplex controller.
2. `/voice/transcribe` returns a non-empty transcript. No-speech and unusable
   capture return to Listening without creating a run.
3. The transcript is submitted as an ordinary Agent run input. A source marker
   may identify it as voice for UI telemetry, but it does not change execution
   semantics.
4. `AgentSseClient` parses typed assistant deltas and the existing Agent
   controller exposes them to the voice playback consumer in order.
5. Only sanitized visible assistant text enters `VoicePlaybackService` and
   `/voice/stream`. Tool names, arguments, approval cards, activity metadata,
   private VTS codes, segment IDs, and transport timings never enter TTS,
   subtitles, history, memory, or logs.
6. Playback closes once at the Agent terminal boundary. Microphone capture
   reopens only after the same terminal/cancellation boundary used by typed
   turns.
7. On SSE loss, the client reconnects using the durable Agent cursor. Missed
   transient deltas are reconciled from the persisted assistant message once;
   `(run_id, assistant_message_id, delta_sequence)` prevents duplicate speech.
8. Approval/trust pause, continue, cancel, session replacement, and view
   shutdown preserve the existing Agent state machine and are idempotent for
   capture and playback.

### Memory and reply metadata

Deleting `ConversationCoordinator` must not delete behavior that was merely
located there. Before removal:

- Agent run context must retrieve the same bounded memory records when the
  active settings permit sharing. The retrieval result is passed through the
  existing Agent context path and remains subject to memory policy.
- Agent terminal completion must invoke the existing best-effort
  `RememberTurnAsync`/learning behavior using sanitized visible user and
  assistant text. A memory failure never fails an otherwise completed Agent
  response.
- The metadata policy and VTS action handling must be extracted into a focused
  Agent reply policy seam. It strips private markers before visible deltas and
  durable persistence, validates VTS codes against the current catalog, and
  dispatches at most the already-supported bounded actions. VTS failure is
  non-critical to the text result.
- Activity records use the existing safe role/content/emotion fields and do not
  inherit Chat-specific route/type names after the contract update.

The Agent model/tool contract remains authoritative for tool execution. A
private marker or instruction cannot grant a Broker scope or bypass approval.

## Deletion inventory

### Delete after replacement behavior is covered

- `backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs` and its
  `/chat` mapping.
- `ConversationCoordinator`, `IConversationCoordinator`, `ChatRequest`,
  `ChatEvent`, Chat correlation/frame types, and Chat-only turn/session queue
  modules.
- Chat-only service registrations in `Program.cs`, including the coordinator
  and Chat-specific gates/resolvers that have no Agent caller.
- `WorkerChatRequest`, `IWorkerClient.GenerateChatAsync`, and the Chat
  generation implementation in `LoopbackWorkerClient`.
- Worker v1 Chat message constants/validation (`chat.*`) and Chat-only memory
  extraction messages if no non-Chat caller remains after the memory migration.
- Chat-only backend tests, route fixtures, benchmarks, and compatibility
  assertions.
- `BackendSession.ChatUri`, `ChatWebSocketClient`, and Chat construction in
  the production WinUI composition after the Agent text/voice path is green.

### Keep and verify independently

- `/voice/transcribe`, `/voice/stream`, Voice Engine/TTS, STT, VTS, observation,
  health, settings, sessions, history, memory, and status endpoints.
- `AgentV1Endpoints`, `AgentV1Service`, Harness Core, Agent SSE replay, Broker
  policy/approval/trust, and durable outbox/history.
- `IRuntimeWorkerClient`, `IRuntimeProtocolClient`, and `IVoiceProtocolClient`
  behavior required by non-conversation device endpoints.
- VTS catalog/action runtime behavior, with the Agent reply policy as its
  caller after Chat removal.
- The visual conversation page and voice playback/capture modules until the
  naming cleanup is intentionally scheduled.

The broad `IWorkerClient` adapter is not kept merely for historical naming. If
only device methods remain, the implementation must expose capability-specific
seams and remove Chat terminology from the surviving interface.

## Migration order

1. **Inventory and characterization** — identify every production/test caller
   of Chat generation, memory extraction, VTS catalog/action, and the voice
   transcript callback. Freeze the current Agent and device acceptance suites.
2. **Agent reply behavior** — move memory context/learning and metadata/VTS
   handling to Agent-owned focused modules. Add deterministic tests before
   deleting the old implementation.
3. **Agent voice path** — add typed assistant delta delivery through the Agent
   client/controller and route `VoiceConversationService` transcripts to Agent.
   Prove de-duplication, approval pause, cancel, reconnect, and persisted
   fallback.
4. **Backend Chat deletion** — remove the public endpoint, registrations, Chat
   DTOs/gates/coordinator, Worker Chat request/branch, and Chat-only tests.
5. **Client cleanup** — remove production Chat WebSocket construction and stale
   mode branches. Keep `ChatView` only as a view name if renaming is not part of
   the behavior change.
6. **Contract/documentation update** — update route JSON/checker, architecture,
   `CONTEXT.md`, ADR 0007, runtime behavior, README, CLI help, and verification
   notes to state Agent-only. Remove outdated Chat claims rather than leaving
   compatibility language.
7. **Release verification** — run sequential Python, .NET, WinUI, route, docs,
   and Release-build gates; perform manual microphone, speaker, and VTS smoke
   checks.

The migration never leaves a production branch that silently chooses Chat when
Agent fails. If an Agent run fails, it returns its bounded error and the user
can retry; it does not fall back to a deleted protocol.

## Failure and lifecycle rules

- Duplicate run creation uses the existing idempotency key and cannot append a
  second user turn or start a second voice playback stream.
- Stale Agent deltas are ignored by run/message/sequence identity. A sequence
  gap triggers reconnect/reconciliation rather than guessed text.
- A closed or failed Broker lease prevents replacement execution; the existing
  fail-closed session rotation rule remains unchanged.
- Agent cancellation is propagated to model, tool, SSE subscription, voice
  playback, and microphone capture. Late STT or worker data cannot update a
  newer run.
- TTS/VTS failure preserves the durable Agent text result and reports a bounded
  voice-unavailable state. It never silently changes voice settings.
- Unknown `/chat` requests receive normal unknown-path behavior. No redirect,
  alias, deprecation response, or feature flag is introduced.

## Verification matrix

### Public contract and static deletion checks

- Route contract contains 54 HTTP entries and exactly one WebSocket entry for
  `/voice/stream`; `/chat` is absent.
- ASP.NET endpoint tests prove `/chat` is not mapped.
- Repository checks prove no production code constructs a Chat WebSocket,
  calls `GenerateChatAsync`, or registers `IConversationCoordinator`.
- Worker v1 tests prove non-conversation voice/STT/VTS/status behavior remains
  available while Chat message types are rejected or absent.

### Agent and memory behavior

- Typed text creates one Agent run and one ordered durable history pair.
- Agent context includes bounded memory when settings permit it; learning and
  best-effort persistence match the prior behavior.
- Agent reply policy strips private markers, validates VTS codes, emits safe
  visible text, and does not leak metadata into history/activity/diagnostics.
- Existing policy, approval first-writer-wins, trust, path containment,
  cancellation, idempotency, SSE replay, and reconciliation tests remain green.

### Voice behavior

- Transcript flow is STT -> Agent run -> typed SSE assistant delta -> text-only
  TTS/VTS -> one terminal close.
- No-speech, unusable input, approval/trust pause, continue, cancel, session
  replacement, disconnect, stale delta, duplicate delta, and missed-transient
  fallback cases are deterministic tests.
- `MediaPlayer.Play()` still occurs before subtitle notification; subtitle/VTS
  failure cannot delay or duplicate audio.
- Physical microphone, speaker, and VTube Studio checks remain manual gates.

### Build and documentation

- Run the configured sequential `.venv\Scripts\python.exe -m pytest`, .NET,
  WinUI, route matrix, docs, and Release build commands. Heavy suites are not
  run in parallel.
- `CONTEXT.md`, ADR 0007, architecture, runtime behavior, route matrix,
  README, CLI help, and verification docs contain no claim that Chat is a
  supported generation mode after the cutover.
- `git diff --check` and a scoped status review prove unrelated dirty files are
  not staged by the migration.

### Implementation retirement boundary (2026-08-29)

Tasks 5–7 implement the public, Worker-side, and desktop retirement boundary:
the ASP.NET host has no `/chat` route, Worker v1 retains runtime/voice/VTS/
observation adapters only, and Agent generation uses Worker v2 plus the
authenticated HTTP/SSE contract. The WinUI composer now has one static Agent
indicator and a Standard/Full Access preset selector with no Chat mode branch
or workspace editor. Any future OS-enforced filesystem/network sandbox remains
a separate follow-up project.

## Rollback and follow-up

Rollback is a versioned build/release rollback to the last known-good Agent
release. Reintroducing `/chat` would require a new reviewed compatibility
decision and is not an emergency fallback.

The OpenCode-style system-prompt composer is the next independent project. It
may add a typed prompt context and per-step digest after Agent-only transport
is stable, but it must not reintroduce Chat or duplicate the conversation
orchestration seam.

## Approval boundary

Approval of this spec authorizes writing its implementation plan and focused
tests. It does not authorize staging unrelated dirty work or making the code
changes before that plan is reviewed and execution is explicitly selected.
