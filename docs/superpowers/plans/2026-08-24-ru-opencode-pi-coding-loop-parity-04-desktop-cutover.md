# Ru Coding-Loop Parity Phase 4: Desktop Cutover and Parity Acceptance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Ru Desktop Agent onto the shared Harness API/SSE, retire the old Python-owned general Agent loop and three-profile model, reconcile all current documentation, and collect the evidence required for the milestone-1 coding-loop parity claim.

**Architecture:** Ordinary Chat stays on its existing WebSocket; Agent prompt creation, events, approvals, payloads, cancellation, Continue, and reconciliation use authenticated HTTP/SSE through `RuAgent.Client`; a focused `AgentRunPane` and view model keep Harness UI logic out of the already-large `ChatView`; the whole-run v2 cutover occurs only after deterministic and real-model acceptance.

**Tech Stack:** WinUI 3, .NET 10, xUnit, ASP.NET Core HTTP/SSE, RuAgent.Client, deterministic GUI Harness, Python pytest, route/documentation contracts, disposable Git repositories.

---

## Checkpoint outcome

This phase is complete only when:

- Desktop and CLI observe and control the same headless run.
- Product modes are Chat and Agent; Standard/Full Access are immutable Agent-session permission presets.
- Agent prompts are never submitted through Chat WebSocket, and visible history is delivered once by the Harness outbox.
- The old general Worker-owned model/tool receipt loop and legacy Full execution branch are removed after cutover.
- All deterministic gates pass and a separately approved real-model run succeeds from both CLI and Desktop in disposable repositories while preserving unrelated dirty work.
- Current docs state the current-Windows-user trust boundary and do not claim overall OpenCode/Pi parity or OS sandboxing.

## Task 1: Add a strict Desktop adapter over the shared Agent client

**Files:**

- Create `gui-winui3/Services/AgentRunClientAdapter.cs`.
- Create `gui-winui3/Services/AgentRunModel.cs`.
- Create `gui-winui3/Services/AgentEventModel.cs`.
- Create `gui-winui3/Services/AgentPayloadModel.cs`.
- Create `gui-winui3.Tests/AgentRunClientAdapterTests.cs`.
- Create `gui-winui3.Tests/AgentRunModelTests.cs`.
- Create `gui-winui3.Tests/AgentEventModelTests.cs`.
- Modify `gui-winui3/Services/RuAppComposition.cs`.

- [ ] Reuse the shared Client golden-contract suite and write RED Desktop mapping tests for all run states, durable safe events, transient frames, approvals, trust, Continue, reconciliation, policy responses, private payload errors, and unknown version fail-closed behavior.
- [ ] Add RED delegation tests proving Desktop obtains `PublicHostSession` through start-or-attach and calls shared `RuAgent.Client.AgentApiClient`/`AgentSseClient`, never Worker/Broker descriptors or a second raw `HttpClient`/SSE parser.
- [ ] Add RED UI-dispatch tests for cursor persistence, duplicate durable event suppression, terminal-state completion, bearer/session refresh through Client start-or-attach, and cancellation of a pending reconnect.
- [ ] Add RED privacy tests proving raw payloads never enter Debug output, exception text, UI automation names, Chat messages, diagnostic events, or model `ToString()` values.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentRunClientAdapter|FullyQualifiedName~AgentRunModel|FullyQualifiedName~AgentEventModel"
```

Expected RED: new adapter/models absent.

- [ ] Implement a thin Dispatcher-aware adapter over `RuAgent.Client`; map shared public DTOs to immutable Desktop models before UI use.
- [ ] Keep shared `AgentSseClient` receive ownership to one instance per attached run; Desktop consumers receive dispatched typed events and never parse the network stream themselves.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(desktop): add harness api and sse client`.

## Task 2: Separate Agent session preset/workspace/run state from Chat mode

**Files:**

- Replace/refactor `gui-winui3/Services/AgentSessionService.cs`.
- Create `gui-winui3/Services/AgentSessionSelection.cs`.
- Create `gui-winui3/Services/AgentRunAttachmentStore.cs`.
- Create `gui-winui3/Services/WorkspaceSelectionService.cs`.
- Modify `gui-winui3.Tests/AgentSessionContractTests.cs`.
- Modify `gui-winui3.Tests/AgentSessionGrantModelTests.cs` as legacy coverage, then delete it when the model becomes unused.
- Create `gui-winui3.Tests/AgentRunAttachmentStoreTests.cs`.
- Create `gui-winui3.Tests/WorkspaceSelectionServiceTests.cs`.

- [ ] Write RED session tests for `RuAppMode { Chat, Agent }` plus separate `AgentPermissionPreset { Standard, FullAccess }`; Full is never a third mode.
- [ ] Add RED preset tests: default Standard; Full warning confirmation persists one session selection through the policy user-control API and returns its version; run creation sends that exact version; changing preset affects next run; new/switch session resets Full to Standard unless that exact session has an explicit current selection; stored legacy `full` maps to Agent+Full once.
- [ ] Add RED workspace tests: Agent send requires a user-selected canonical workspace; no repository marker silently selects/trusts; workspace identity move clears trust; picker invocation occurs only from explicit user UI action.
- [ ] Add RED attachment tests: session→active/historical run ID plus last durable sequence persists without private event payloads; reopening attaches rather than creates; terminal run permits a new run; another active workspace returns `workspace_busy` visibly.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentSession|FullyQualifiedName~AgentRunAttachment|FullyQualifiedName~WorkspaceSelection"
```

Expected RED: old three-mode assertions fail.

- [ ] Implement focused state services with no HTTP or XAML dependencies. Do not store Full as a global default.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `refactor(desktop): separate agent presets from mode`.

## Task 3: Build maintainable Agent cards in a dedicated pane

**Files:**

- Create `gui-winui3/Views/AgentRunPane.xaml`.
- Create `gui-winui3/Views/AgentRunPane.xaml.cs`.
- Create `gui-winui3/ViewModels/AgentRunViewModel.cs`.
- Create `gui-winui3/ViewModels/AgentCardViewModel.cs`.
- Create `gui-winui3/ViewModels/AgentApprovalViewModel.cs`.
- Create `gui-winui3/ViewModels/AgentTrustViewModel.cs`.
- Create `gui-winui3/ViewModels/AgentReconcileViewModel.cs`.
- Create `gui-winui3/Services/AgentEventProjector.cs`.
- Create `gui-winui3.Tests/AgentEventProjectorTests.cs`.
- Create `gui-winui3.Tests/AgentRunViewModelTests.cs`.
- Modify `gui-winui3.Tests/AgentHarnessUiAutomationSourceContractTests.cs`.
- Modify `gui-winui3/Views/ChatView.xaml` and `gui-winui3/Views/ChatView.xaml.cs` only to host/switch the pane and forward explicit user intents.

- [ ] Write RED event-projection tests for distinct cards: model activity, file discovery/change, terminal command, stdout/stderr, Git state, approval, trust, warning/error, Continue, reconciliation, and final answer. Tool/control events must never become assistant chat bubbles.
- [ ] Add RED boundedness tests: merge transient terminal frames per execution with head/tail UI cap, display dropped-frame count, retain durable final snapshot after reconnect, and dispose per-run buffers at terminal state.
- [ ] Add RED UI-source contracts for automation IDs, keyboard reachability, accessible names, no raw command/output in automation names, and no approval action reachable through voice commands.
- [ ] Add a maintainability guard test: `ChatView.xaml.cs` must not gain Agent HTTP/SSE parsing or card-projection methods; `AgentRunPane.xaml.cs` is event forwarding only; orchestration belongs in `AgentRunViewModel`.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentEventProjector|FullyQualifiedName~AgentRunViewModel|FullyQualifiedName~AgentHarnessUiAutomation"
```

Expected RED: pane/view models absent.

- [ ] Implement the pane using existing Ru visual tokens/responsive policies. Preserve no-emoji/no-ASCII-emoticon Chat contracts and keep private VTS/voice metadata out of cards.
- [ ] Render private approval/diff/output preview only after explicit fetch; unload/clear it when card/run/session changes.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(desktop): render typed agent run cards`.

## Task 4: Route Agent prompt, history, attach, and final answer correctly

**Files:**

- Modify `gui-winui3/Views/ChatView.xaml`.
- Modify `gui-winui3/Views/ChatView.xaml.cs`.
- Modify `gui-winui3/Services/ChatWebSocketClient.cs` only to reject/deprecate Agent sends.
- Modify `gui-winui3/ViewModels/AgentRunViewModel.cs`.
- Create `gui-winui3.Tests/AgentPromptRoutingTests.cs`.
- Create `gui-winui3.Tests/AgentHistoryDeliveryTests.cs`.
- Create `gui-winui3.Tests/AgentAttachTests.cs`.
- Create `gui-winui3.Tests/AgentFinalAnswerVoiceBoundaryTests.cs`.
- Modify `backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs` for legacy-mode ingress only.

- [ ] Add RED tests: Chat send uses Chat WebSocket; Agent send calls `POST /agent/v1/runs` with session/workspace/prompt/preset/idempotency key and never calls `ChatWebSocketClient.SendMessageAsync`.
- [ ] Add RED duplicate-history test: Desktop may optimistically display the submitted prompt, but session reload contains one outbox-delivered user message and one final assistant message; reconnect/retry never duplicates either.
- [ ] Add RED attach tests for reopening/switching session, resuming after last durable sequence, simultaneous CLI attach, client disconnect leaving run active, and terminal-state final snapshot.
- [ ] Add RED final-answer tests: only final visible assistant text enters the Chat transcript and optional TTS; tool output/cards/approvals never enter TTS, VTS metadata, memory extraction, examples, or Chat history.
- [ ] Add RED voice boundary tests: hands-free input cannot create an Agent coding run or answer trust/terminal/Git approval in milestone 1; explicit typed/click UI is required.
- [ ] Run RED focused GUI and Backend tests sequentially.
- [ ] Implement routing and optimistic-display reconciliation using stable source message IDs from the API response.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(desktop): route agent turns through harness api`.

## Task 5: Wire trust, approvals, Stop, Continue, payload, and reconcile actions

**Files:**

- Modify `gui-winui3/ViewModels/AgentRunViewModel.cs`.
- Modify `gui-winui3/ViewModels/AgentApprovalViewModel.cs`.
- Modify `gui-winui3/ViewModels/AgentTrustViewModel.cs`.
- Modify `gui-winui3/ViewModels/AgentReconcileViewModel.cs`.
- Modify `gui-winui3/Views/AgentRunPane.xaml`.
- Modify `gui-winui3/Services/AgentEventProjector.cs`.
- Replace legacy `gui-winui3/Services/AgentApprovalModel.cs` with new API-backed approval state; delete after no references remain.
- Modify `gui-winui3.Tests/AgentApprovalTests.cs` and `gui-winui3.Tests/AgentApprovalUiContractTests.cs`.
- Create `gui-winui3.Tests/AgentTrustUiContractTests.cs`.
- Create `gui-winui3.Tests/AgentReconcileUiContractTests.cs`.
- Create `gui-winui3.Tests/AgentCancellationUiTests.cs`.

- [ ] Add RED trust UI tests for canonical identity hash display, current-Windows-user warning, approve/deny, stale/moved response, and Full non-bypass.
- [ ] Add RED approval UI tests for Allow once/run/project/Deny; exact normalized matcher/scope preview; expiry; first-client-wins; stale response; reusable-rule-save failure notice; private preview fetched on demand only.
- [ ] Add RED Stop tests: one idempotent cancel request, Worker cancellation, Broker process-tree cancellation visible through durable final state, and no assumption that side effects were impossible.
- [ ] Add RED Continue tests only for `WaitingUser`, showing renewed 32/64 budget window; no Continue for provider `context_limit_reached`.
- [ ] Add RED reconcile UI tests: show last canonical intent plus fresh workspace/Git snapshot; submit Applied/NotApplied/Unknown; stale hash rejected; Unknown explains mandatory inspection; never offer “retry old command”.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentTrust|FullyQualifiedName~AgentReconcile|FullyQualifiedName~AgentCancellation"
```

Expected RED: API-backed actions absent/legacy two-button approval assertions fail.

- [ ] Implement all actions through `AgentRunClientAdapter` over the shared Client; disable controls while an exact request is in flight, but reconcile from authoritative SSE/HTTP state rather than optimistic success.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(desktop): control durable agent runs`.

## Task 6: Translate legacy Full inputs and make v2 the default

**Files:**

- Modify `backend-dotnet/RuBackend/Agent/CapabilityProfile.cs` or replace with a compatibility translator.
- Modify `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs` callers.
- Modify `backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs` compatibility parsing.
- Modify `backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs` and v1 compatibility branches.
- Modify `gui-winui3/Services/ChatWebSocketClient.cs` and settings DTOs.
- Modify `backend-dotnet/RuBackend.Tests/Conversation/AgentModeProfileTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`.
- Modify `gui-winui3.Tests/AgentSessionContractTests.cs`.
- Modify `gui-winui3.Tests/SettingsDtoTests.cs`.
- Modify `backend-dotnet/RuBackend/Harness/HarnessFeatureOptions.cs`.

- [ ] Add RED translation tests: legacy wire/stored `full` maps to Agent+Full for a new Harness run; legacy `agent` maps to Agent+Standard; unknown maps to Chat/fail-closed as current compatibility requires; no model/tool implementation branches on Full after translation.
- [ ] Add RED whole-run routing tests: run snapshots v1/v2 exactly once at creation; no in-flight run changes version; only one writable loop handles a run; default changes to v2 only after all Desktop tests in Tasks 1–5 pass.
- [ ] Add RED settings tests: current UI stores mode and preset separately; Full is not global/sticky; older settings load and are rewritten only after successful parse.
- [ ] Run RED across Backend/Broker/GUI profile suites sequentially.
- [ ] Implement compatibility translators at ingress. Do not let `CapabilityProfile.Full` survive into Core, Worker v2, policy snapshot builder, Broker v2 evaluator, CLI, or Desktop view models.
- [ ] Change `RuBackend:AgentHarness:Version` default to `v2` only in this task after focused GREEN evidence.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `refactor(agent): make harness v2 the default loop`.

## Task 7: Remove the old general Agent loop and obsolete branches

**Files:**

- Remove general tool-loop ownership from `src/ru_runtime/worker/v1/handlers.py` while preserving ordinary Chat and deterministic native volume behavior.
- Remove now-unused general action-loop branches from `src/ru_runtime/worker/v1/action_loop.py` only after v2 equivalents and volume tests prove coverage.
- Remove v1 Host tool-receipt handling from `backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs` if no Chat/native-volume caller needs it.
- Delete `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs`, `backend-dotnet/RuBackend/Agent/AgentApprovalCoordinator.cs`, `backend-dotnet/RuBackend/Agent/AgentApprovalStore.cs`, `backend-dotnet/RuBackend/Agent/AgentSessionGrantStore.cs`, `backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs`, `backend-dotnet/RuBackend/Agent/IAgentBrokerClient.cs`, `backend-dotnet/RuBackend/Agent/AgentModels.cs`, `backend-dotnet/RuBackend/Agent/CapabilityProfile.cs`, `gui-winui3/Services/AgentSessionGrantModel.cs`, and `gui-winui3/Services/AgentApprovalModel.cs` only after the reference-manifest command below proves each production type is unused or superseded.
- Modify or delete v1-only branches in `backend-dotnet/RuAgent.Protocol/AgentEnvelopeCodec.cs`, `backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs`, `backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs`, `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs`, and `backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs` only after the reference manifest is empty; retain only documented ingress compatibility.
- Delete or rewrite `backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs`, `backend-dotnet/RuBackend.Tests/Agent/AgentApprovalCoordinatorTests.cs`, `backend-dotnet/RuBackend.Tests/Agent/AgentBrokerClientTests.cs`, and obsolete GUI grant/profile tests only after equivalent v2 coverage is green.

- [ ] Before deletion, produce an exact reference manifest:

```powershell
rg -n "AgentTurnCoordinator|AgentApprovalCoordinator|AgentApprovalStore|AgentSessionGrantStore|CapabilityProfile\.Full|chat\.tool\.request|wait_for_tool_result|MAX_TOOL_CALLS_PER_TURN|RuAppMode\.Full" D:\LLM\src D:\LLM\backend-dotnet D:\LLM\gui-winui3 D:\LLM\tests D:\LLM\gui-winui3.Tests
```

- [ ] Add/retain RED regression tests that ordinary Chat streams normally; deterministic volume command still emits one verified native mutation outcome; private outcome metadata never becomes Chat/TTS/history; Worker v2 remains one step; Agent uses only Harness API.
- [ ] Add source-contract tests that Worker v1 no longer owns a general multi-step model/tool receipt loop and no Desktop code sends Agent mode to Chat WebSocket.
- [ ] Delete code in small compile-green groups. After each group, run the closest Python or .NET focused suite before the next deletion.
- [ ] Run full Python Worker, Backend, Broker, and GUI tests sequentially. Expected: zero failures and no production reference in the manifest except explicit legacy input translators.
- [ ] Commit: `refactor(agent): remove legacy writable agent loop`.

## Task 8: Add the superseding ADR and reconcile every current document

**Files:**

- Create `docs/adr/0008-headless-agent-harness-and-permission-presets.md`.
- Modify `docs/adr/0007-mode-capability-profiles.md` to state it is superseded by ADR 0008 without rewriting historical rationale.
- Modify `CONTEXT.md` with Harness Core, Agent Run, Model Step, Policy Context, permission preset, workspace trust, execution/reconciliation, and current-user boundary terms.
- Modify `docs/architecture.md`.
- Rewrite `docs/operations/agent-tools.md` for current v2 behavior.
- Modify `docs/aspnetcore-route-matrix.md`.
- Modify `README.md`, `backend-dotnet/README.md`, and `backend-dotnet/RuAgent.Cli/README.md`.
- Modify `docs/README.md` so the design/plans become historical implementation records only after completion; current docs remain the operating contract.
- Modify `TODO.md` for milestone-2/3 backlog only if missing; retain the separate OS sandbox project unchanged.
- Modify `tests/test_current_documentation.py`.
- Inspect and delete current operational documents made wholly obsolete; retain dated plans/specs as history.

- [ ] Write RED documentation tests before editing docs: ADR list includes 0008; ADR 0007 says superseded; modes are Chat/Agent; presets Standard/Full; Public Host owns Worker/Broker; Core owns outer loop; Worker v2 one step; routes 54+2; local-user/non-sandbox/network limitation; CLI/Desktop shared run; parity wording limited to coding loop.
- [ ] Add RED tests rejecting stale claims: Agent lacks terminal, Full is a separate capability loop, WinUI owns three processes, Worker waits for general tool receipts, permission rules isolate network/filesystem, or overall OpenCode/Pi parity is complete.
- [ ] Run RED:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_current_documentation.py D:\LLM\tests\test_aspnetcore_route_matrix.py
```

Expected RED: current docs still contain transitional/legacy claims.

- [ ] Update docs atomically. ADR 0008 must include consequences: current-user authority, Full warning, one runtime, durability, no OS sandbox, and future session/extension milestones.
- [ ] Delete only genuinely obsolete current docs after their non-obsolete guidance is consolidated. Never delete dated approval/design history solely because implementation completed.
- [ ] Run GREEN. Expected: zero failures and route docs exactly match canonical data.
- [ ] Commit: `docs(agent): document the headless coding harness`.

## Task 9: Run the full deterministic release matrix

**Files:**

- Modify `build.bat`, `.github/workflows/ci.yml`, and `.github/workflows/gui-harness.yml` only if a project/gate is still omitted.
- Modify `scripts/run_gui_voice_harness.ps1`, `gui-winui3.Harness/UiAutomationScenario.cs`, and `gui-winui3.Harness/Program.cs` for deterministic Agent v2 scenarios without adding live-device mutation.

- [ ] Verify `.github/workflows/ci.yml` still runs Ruff, Mypy, and pytest on both Python 3.11 and 3.13, all Core/Persistence/Hosting/Client/Broker/Connector/Backend/CLI/WinUI test projects, Release GUI build, and native overlay tests; verify `.github/workflows/gui-harness.yml` remains manual/self-hosted for deterministic GUI scenarios.

- [ ] Scan production sources for placeholders, retired types, duplicate ownership, and stale route values:

```powershell
rg -n "TBD|TODO|FIXME|NotImplementedException|CapabilityProfile\.Full|RuAppMode\.Full|AgentTurnCoordinator|wait_for_tool_result|41 HTTP|WinUI.*owns" D:\LLM\src D:\LLM\backend-dotnet D:\LLM\gui-winui3 D:\LLM\README.md D:\LLM\CONTEXT.md D:\LLM\docs
```

Expected: only explicit future backlog/historical records match; no production placeholder or retired owner remains.

- [ ] Run the full matrix sequentially, never concurrently:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
& D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
cmd /c D:\LLM\build.bat
```

Expected: every command succeeds, route matrix is 54 HTTP plus 2 WebSocket, Release publish succeeds.

- [ ] Run deterministic Backend/GUI Harness scenarios for run creation, trust, every approval scope, file tools, terminal frames, cancellation, Continue, reconnect, reconciliation, history single-delivery, CLI/Desktop concurrent attach, Full warning, and no-emoji/private-metadata contracts.
- [ ] Confirm deterministic evidence is labeled correctly: it does not prove audible output, microphone, real volume, desktop control, VTS, provider quality, or real-model coding success.
- [ ] Inspect/stage only phase-4 implementation/docs files and commit: `feat(agent): complete desktop harness cutover`.

## Task 10: Perform separately approved real-model parity acceptance

This task requires explicit user authorization because it may spend provider credits, run code, create commits in disposable repositories, and requires Desktop interaction. Do not silently automate the user's desktop or change live audio/device state.

- [ ] Create two disposable Git repositories outside `D:\LLM`, each with a seeded failing test and unrelated dirty work; one also has a separately controlled local-commit case. Record exact absolute paths and deletion/retention choice.
- [ ] Ask for explicit approval before starting provider-backed CLI/Desktop runs and before any UI automation. If approval is not given, stop with implementation “ready for acceptance”; do not claim parity.
- [ ] CLI run: observe inspect → edit intended file → run relevant test/build → repair if needed → preserve unrelated dirty work → final answer; exercise Standard approvals, output streaming, reconnect, and cancel in a separate run.
- [ ] Desktop run: repeat the seeded-bug workflow through the same headless runtime; attach CLI concurrently; verify separate Agent cards, workspace trust, approval preview, Stop, reconnect/session switch, one final Chat-history answer, and no duplicate run.
- [ ] Managed commit run: start with eligible clean index, show exact staged path/diff summary, approve, create a local commit containing only run-attributed changes, and verify unrelated dirty work is excluded. Separately prove a non-empty start index disables managed commit.
- [ ] Inspect `git status --short`, `git show --stat --oneline HEAD`, test output, session history, safe diagnostics, and Harness event sequence. Confirm no known secret/raw command/output entered safe diagnostic JSONL.
- [ ] Record failures as new focused RED tests and return to the owning task; do not waive a failed acceptance item.
- [ ] Only after all acceptance items pass, update the implementation checkpoint/status wording to “OpenCode/Pi coding-loop parity” with the explicit qualifier that overall harness parity, session trees/compaction, extensions/skills/subagents/MCP/LSP/web, and OS sandboxing remain future work.
- [ ] Commit acceptance-only docs/evidence with no secrets or raw private payloads: `docs(agent): record coding loop parity acceptance`.
