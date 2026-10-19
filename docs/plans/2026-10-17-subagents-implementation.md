# Ru Subagents Implementation Plan

status: implemented; read-only revision verified

> 2026-10-19 revision: the user's latest requirements supersede the original role
> settings and user controls below. The current contract is read-only observation
> (two GET endpoints, 45 total routes). Children inherit exact parent pins; explicit
> user-requested model changes resolve only signed-in provider catalogs. Historical
> user-apply proof remains for reconciliation; no new user apply is accepted. See
> [current operations](../operations/subagents.md). Earlier test/UI evidence below
> predates this revision; it is not acceptance evidence for the revised behavior.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Deliver automatic parallel coding and read-only research/review children, with exact inherited models or explicit user-requested signed-in catalog models and visible, durable lifecycle.

**Architecture:** Harness owns durable identities, scheduling and context. Broker remains the only execution authority, including delegated workspace provisioning and result integration. Coding uses a private worktree based on the parent's current files; review reads the parent workspace with mutation and terminal execution denied.

**Tech Stack:** .NET 10, ASP.NET Core, SQLite WAL/encrypted payloads, WinUI 3, Terminal.Gui, TypeScript SDK; existing dependencies unless separately justified.

**Spec:** [Approved design](2026-10-17-subagents-design.md). Baseline: `dev/b69455dd`. Reference implementations: Claude Code, Codex, OpenCode.

Execution is in the current session on `dev`, starting at `b69455dd`.
Tasks 1–9 are implemented: depth-one delegated authority, private dirty snapshots,
encrypted durable lifecycle, pinned models, shared scheduling/budgets,
guarded parent-tool apply/reconciliation and read-only HTTP/RPC/SDK/CLI/WinUI observation.
Historical schema v28 user-apply proof is retained; unexecuted user actions are retired.
The catalog now exposes 18 model tools (17 Broker bindings; `ask_user` stays Host-only)
and the public contract has 45 HTTP routes with no WebSocket.

Historical pre-revision acceptance completed on 2026-10-19: full .NET 6,861 passed; Python 216 passed
and one expected Windows/POSIX skip; SDK typecheck/61 tests/build; full 11-stage
self-contained Release build and smoke of that exact flat product tree all passed.
A final description-only wait clarification separately passed Protocol 151, Core
518 and focused Host 48. The earlier schema-v27 checkpoint's 6,832 .NET passes are
historical evidence, not the current feature's final gate.

The final configured-provider fixture pair completed both roles and applied one
coding file with exact inherited pins, unchanged checks/HEAD and no unexpected
Git changes. The single and delegated cases took 23.84 s and 55.80 s; this one
small case establishes no quality, latency or cost gain. Earlier canceled-review,
setup and QA failures are retained in the evidence directory.
Evidence and RED/GREEN details live in
`artifacts/subagent-implementation-2026-10-17/progress.md`.
Native WinUI interaction was subsequently authorized and exercised on Release:
history/session switching, role settings, view/export, follow-up, child/root Stop
and queued user Apply. Native findings were fixed; GUI 1,504 tests, Release11/11
and final smoke passed again. Evidence: `artifacts/subagent-gui-qa-2026-10-19/`.
This is targeted acceptance; no feature commit/push has been authorized.
Unproven physical artifacts remain bounded named failures requiring an explicit
recovery decision; the implementation never adopts, prunes or retries an uncertain
mutation merely to complete a run.

## Global Constraints

- Preserve single execution authority, fail-closed permissions, model pinning and existing non-delegating behavior. No OS sandbox claim.
- Root only: `agent_spawn`, `agent_send`, `agent_wait`, `agent_cancel`, `agent_apply`. Depth one; two roles: coding/review.
- Defaults: three runnable children/root, eight identities/root, three attempts/agent, 32 model steps/64 tool calls per child attempt. Shared family window: 128 model steps/256 tool calls, including prior root consumption.
- Admission deadline: 15 minutes from creation. Attempt deadline: 15 minutes from admission, including approval waits; messages do not reset deadlines or budget.
- Limits: task/message 16,384 UTF-16 characters; context 64 KiB UTF-8; inline result 32 KiB UTF-8; snapshot 50,000 files/256 MiB, never silently truncated.
- No copied approval tokens/session folder grants. Parent revocation invalidates child dispatch; parent permission increases do not widen existing children.
- Preserve dirty parent files, index and branch. No automatic commit/push; no automatic shared-checkout fallback. Commit only after an explicit request covering these changes.
- Keep new responsibilities in focused Subagents modules. Keep feature exposure disabled until all release gates pass.
- Run process/loopback matrices sequentially with `scripts/test_dotnet.ps1`. Deterministic, native UI and live-provider evidence are separate.

## Review Focus

- Dirty CRLF, deletion and untracked input must survive snapshot without being misreported as child edits (Task 3).
- Absolute deny/ask patterns and canonical aliases must retain authority after workspace relocation (Task 2).
- Four parents waiting together must release drive slots and permit children to finish (Task 6).
- A crash between file write and receipt must never trigger blind replay or rollback over a newer user edit (Task 7).
- Concurrent send/Stop must leave a cancellation tombstone and at most one active child attempt (Tasks 4–5).

## Interface and file map

New contracts live in `RuAgent.Protocol/Subagents/`; core records/store interfaces in `RuAgent.Harness.Core/Subagents/`; store implementations in `RuAgent.Harness.Persistence/Subagents/`; authority/worktree/integration in `RuAgent.Broker/Subagents/`; orchestration in `RuBackend/Harness/Subagents/`. All paths below are relative to `backend-dotnet/` unless prefixed otherwise.

Use `SubagentRole`, `SubagentRecord`, `SubagentAttempt`, `SubagentResult`, `SubagentChangeSet`, `SubagentApplyOutcome` and `DelegatedExecutionScope` as explicit types. Agent IDs remain bounded Protocol-validated strings. Reuse existing `RunId`, `SessionId`, provider selection and receipt types; never represent authority as task text.

Public controls under `/agent/v1/runs/{runId}/subagents`: GET collection; GET `/{agentId}`; POST `/{agentId}/messages`; POST `/{agentId}/cancel`; POST `/{agentId}/apply`. Spawn remains a model tool, not a second execution endpoint. New read responses carry paging for large results.

### Task 1: Strict contracts and tool inventory

**Files:** Create `RuAgent.Protocol/Subagents/SubagentContracts.cs`, `SubagentToolDefinitions.cs`, `SubagentContractValidator.cs`; modify `AgentToolDefinitionCatalog.cs`, `AgentToolScopes.cs`; test `RuAgent.Protocol.Tests/SubagentContractTests.cs`.

**Interfaces:** Produce immutable request/result DTOs for five tools and five HTTP controls; `SubagentContractValidator.Validate(string toolId, JsonElement arguments)` returns a bounded validation result. Definitions remain in Protocol; schema digests flow through the existing catalog.

- [x] Write tests rejecting unknown properties, cross-role apply, oversized UTF-16/UTF-8 inputs, invalid result versions, duplicate/empty wait IDs and unbounded timeout; test all five examples against schemas.
- [x] Run `scripts/test_dotnet.ps1 -ProjectFilter Protocol`; verify new behavioral assertions fail before implementation.
- [x] Implement strict schemas/types/validators and catalog selection. Keep the five definitions unexposed until Task 8; update inventory guards only when exposure changes.
- [x] Repeat Protocol checks; require pass and unchanged existing tool schemas/digests except intentional catalog metadata.

### Task 2: Delegation authority and policy mapping

**Files:** Create `RuAgent.Broker/Subagents/DelegatedExecutionScope.cs`, `DelegationPolicyEvaluator.cs`, `DelegationParentPolicy.cs`; modify `Runtime/BrokerExecutionRuntime.cs`, `Runtime/BrokerExecutionRuntime.Tools.cs`, `Policy/PolicyContextFactory.cs`, `RuBackend/Harness/Broker/InProcessToolGatewayFactory.cs`; test `RuAgent.Broker.Tests/SubagentPolicyTests.cs` and `RuBackend.Tests/Harness/SubagentGatewayTests.cs`.

**Interfaces:** `DelegationPolicyEvaluator` constrains the existing policy decision with the delegated scope. Scope binds root/agent/run, canonical parent/child roots, parent authority version and fixed role. Authorization uses existing evaluate/prepare/execute receipt flow; Host consumes a typed authorized orchestration intent, never a model-supplied authorization flag.

- [x] Test review mutation/bash denial, Plan coding/apply denial, nested/forged/sibling ownership denial, expired lease, stale approval and root revocation.
- [x] Test relocated absolute/path patterns preserve deny-before-ask-before-allow; canonical junction/alias targets cannot bypass scope. Existing root Full Access behavior stays unchanged.
- [x] Run Broker then RuBackend focused projects; confirm failures, implement evaluation and gateway propagation, rerun sequentially to green.
- [x] Record boundary findings in the design; coding exposure remains off if mapping cannot be proven.

### Task 3: Immutable dirty snapshots and owned worktrees

**Files:** Create `RuAgent.Broker/Subagents/Workspaces/SubagentGitAdapter.cs`, `SubagentSnapshotCapture.cs`, `SubagentWorktreeProvisioner.cs`, `SubagentWorkspaceManifest.cs`; test `RuAgent.Broker.Tests/SubagentWorkspaceTests.cs`.

**Interfaces:** Broker-owned `SubagentSnapshotCapture` captures the parent lease into an immutable snapshot/manifest; `SubagentWorktreeProvisioner` returns a validated child workspace identity. Base bytes/manifests live outside the child checkout; no hardlinks to mutable parent files. Git executable/argv are fixed and bounded.

- [x] Use disposable Git fixtures to assert staged/unstaged CRLF edits, tracked deletions and nonignored untracked bytes are preserved; assert parent HEAD, branch, index bytes and working files are unchanged.
- [x] Add executable sentinel hooks/filter/fsmonitor fixtures: none may run during capture/provision/collect. Test denied read, concurrent capture edits, cap overflow, path escape/case collision, repo identity redirection, submodules, sparse and unmerged indexes.
- [x] Run Broker to see failures; implement bounded no-checkout/raw-byte provisioning with all executable Git configuration neutralized or rejected. Do not call user repo setup scripts or copy ignored dependencies/secrets.
- [x] Revalidate common-dir/git-dir/root/Windows directory identity on reuse and cleanup; keep changed or uncollected workspaces. Run Broker to green. This is a required feasibility gate, not a fallback to shared writes.

### Task 4: Durable storage and migration

**Files:** Create core `Subagents/ISubagentStore.cs`, `SubagentAdmission.cs`; create persistence `Subagents/SqliteSubagentStore.cs`, `SqliteSubagentBudgetStore.cs`, encrypted payload helpers in `HarnessPayloadSqlite.cs`, `HarnessSqliteSchema.Subagents.cs`; modify `HarnessSqliteSchema.cs`, `HarnessSqliteSchemaValidator.cs`, `RuBackend/Business/Sessions/SessionStore.Listing.cs`, `HarnessSessionArtifactStore.cs`; test `RuAgent.Harness.Persistence.Tests/SubagentStoreTests.cs`, `SubagentIdentityMigrationTests.cs`.

**Interfaces:** `ISubagentStore` reserves stable identities and records admitted attempts, internal ownership and idempotency. Inbox, results, cancellation and continuation transitions are transactional. Budget store reserves by stable invocation identity before dispatch. Result/task/context/message content uses encrypted payload storage, not plaintext SQL columns.

- [x] Test migration v19→v20 preserves roots/approvals/trust; restart and duplicate create/send create one identity/attempt; concurrent send/cancel produces at most one active run and honors tombstones.
- [x] Test delivery dedup, eight-identity/three-attempt caps, encrypted content, internal-session listing exclusion before pagination and descendant artifact ownership/deletion.
- [x] Run Persistence to confirm failures; implement migration, indexes/foreign keys/records and store transactions; rerun to green.
- [x] Test reservation crash/retry cannot reset or double-charge family budget; unreconciled invocation is not refunded, root pre-delegation counters count toward the pool.

### Task 5: Child orchestration, context and pinned models

**Files:** Create `RuBackend/Harness/Subagents/SubagentControlDispatcher.cs`, `SubagentRunFactory.cs`, `SubagentSelectionResolver.cs`, `SubagentContextBuilder.cs`, `SubagentCodingResultPublisher.cs`; modify `Business/Settings/SettingsSnapshot.cs`, `Business/Settings/SettingsStore.cs`, `Hosting/PublicHostServiceRegistration.cs`, `Harness/HarnessSessionDeletionCoordinator.cs`; test `RuBackend.Tests/Harness/SubagentLaunchTests.cs`, `SubagentSelectionTests.cs`.

**Interfaces:** `SubagentControlDispatcher` consumes call-bound Broker authorization and Task 4 storage; `SubagentRunFactory` prepares admitted attempts. Selection resolver returns the existing pinned selection type; absence of a user-requested model means exact parent inheritance. An explicit prompt request resolves only signed-in catalogs. Collector derives file changes/check receipts, never trusts model prose as proof.

- [x] Test inherit/override snapshots across configuration edits, unavailable model/key/protocol errors without fallback, fresh bounded context, safe-boundary messages and new run on continuation.
- [x] Test queued-send vs Stop, canceled agent cannot resume, root terminal rejects new operations; root Stop/deletion persist family cancellation before further admission and cancel providers/Broker process trees.
- [x] Run RuBackend to confirm failures; implement orchestration via existing run factories, explicit injected collaborators and encrypted context/results; rerun to green.
- [x] Keep internal replies out of user conversation/outbox; parent receives each result once with observed file hashes, receipt-backed checks and paged payload references.

### Task 6: Scheduler, deferred wait and recovery

**Files:** Create `RuBackend/Harness/Subagents/SubagentScheduler.cs`, `SubagentWaitCoordinator.cs`, `SubagentRecoveryCoordinator.cs`; modify `Harness/AgentRuntimeLoopService.cs`, `Harness/Reconciliation/HarnessRecoveryScanner.cs`, core `HarnessRunState.cs`, `AgentRunStateMachine.cs`, `AgentRunCoordinator.cs`, persistence state serialization/constraints, `Harness/RunUsageTracker.cs`; test `RuBackend.Tests/Harness/SubagentSchedulingTests.cs`, `SubagentRecoveryTests.cs`.

**Interfaces:** `SubagentWaitCoordinator` persists `waiting_subagents` and a deferred tool result; wakes by durable result/deadline and resumes existing proposal once. `SubagentScheduler.AdmitAsync(CancellationToken)` uses the existing global drive owner with per-root fairness, not a second independent worker pool.

- [x] Test four waiting parents with children under global four drive passes; wait releases provider/worker slots, delivery wakes once, no polling model calls or duplicate authorization/spawn.
- [x] Test 3 runnable children/root, admission/attempt deadlines, approval wait expiry, budget exhaustion and explicit Continue family window; Stop remains available with no budget.
- [x] Test restart while queued/running/waiting, ambiguous mutation recovery and pending approval invalidation; parent terminalization records child outcomes/cancellation before relinquishing ownership.
- [x] Run Core, Persistence, RuBackend sequentially to confirm failures; implement state and recovery hooks, then rerun each to green. Aggregate actual usage; missing usage is incomplete.

### Task 7: Result collection and guarded apply

**Files:** Create `RuAgent.Broker/Subagents/Integration/SubagentChangeSetCollector.cs`, `SubagentApplyAdapter.cs`; persistence uses `RuAgent.Harness.Persistence/Subagents/SqliteSubagentApplyJournal*.cs` and `SqliteHarnessEventStore.SubagentReconciliation*.cs`; reuse `Tools/Files/FileMutationGuard.cs` and recovery storage; test `RuAgent.Broker.Tests/SubagentApplyTests.cs`, `RuBackend.Tests/Harness/SubagentCodingPublicationTests.Apply.cs`, `SubagentNativeControlTests.Recovery.cs`.

**Interfaces:** `CollectAsync(snapshotId, workspaceIdentity, CancellationToken)` produces `SubagentChangeSet` against frozen base; `ApplyAsync(SubagentApplyRequest, CancellationToken)` produces `SubagentApplyOutcome` bound to root, agent, result version and stable invocation. Journal stores per-file intent/base/proposed hash/receipt, encrypted content and replay state.

- [x] Test dirty parent edits are excluded from child delta; stale base/create/delete conflicts preserve parent files and all-files preflight denies unsupported entries before any write.
- [x] Test actual mutation uses same held file identity/hash guard, permissions rechecked, protected parent paths cannot be reached through a forged change set, and model summary/hash/HEAD tampering cannot choose an apply target.
- [x] Inject crashes before write, during write and before receipt; duplicate apply returns saved applied/partial/unknown state. New user edits prevent blind rollback/replay. Do not promise multi-file atomicity.
- [x] Run Broker then RuBackend to confirm failures; implement collector/apply/journal using existing authority and recovery; rerun to green.

Verified internal checkpoint: schema v27 and the native explicit reconciliation path pass the full .NET matrix in `artifacts/subagent-implementation-2026-10-17/task7-reconciliation-full.log`. This completes the internal apply gate; public controls, UI and live-provider acceptance remain separate.

### Task 8: Read-only public observation, RPC/SDK and parent model tools

**Files:** Create `RuBackend/Harness/Subagents/SubagentEndpoints.cs`, `RuAgent.Client/AgentApiClient.Subagents.cs`, `RuAgent.Cli/Rpc/RpcSubagentDispatcher.cs`, `sdk/ru-agent/src/subagents.ts`; modify `Harness/AgentV1Endpoints.cs`, `Harness/Prompting/AgentToolRuntimeProjection.cs`, `RuAgent.Broker/Tools/ToolCatalog.cs`, `RuAgent.Cli/Rpc/RpcDispatcher.cs`, SDK `types.ts`, `client.ts`, `run.ts`, `index.ts`, `public-route-contract.json`; add corresponding Protocol/Client/CLI/Backend/SDK contract tests.

**Interfaces:** HTTP routes match the file-map declaration. Client/SDK expose list/get under the owning root; user mutations are refused, while only parent model tools send/cancel/apply; events carry agent/attempt IDs and result version. Strict serialization of `waiting_subagents` must work in every client.

- [x] Test wrong root, unknown agent, terminal root, duplicate controls, input bounds, result paging, bearer auth and SSE reconnect/dedup. RPC stdout stays protocol-only; transport detach does not cancel children.
- [x] Run affected .NET projects sequentially and SDK `npm run typecheck`, `npm test`, `npm run build`; observe failing cases before implementation.
- [x] Implement contracts and entry points through coordinator/gateway; expose the five tools only after Tasks 2–7 pass. Update manifest versions/digests, handshake, route inventory and strict decoder guards together.
- [x] Repeat focused checks to green; test old roots without subagents retain existing replies/order.

### Task 9: Read-only GUI/CLI tree

**Files:** Create `gui-winui3/Services/SubagentTreeModel.cs`, `gui-winui3/Views/SubagentTreeView.xaml` and code-behind, `backend-dotnet/RuAgent.Cli/Subagents/SubagentPresenter.cs`; modify GUI `Services/AgentRunClientAdapter.cs`, `Services/AgentRunController.cs`, `Models/SettingsModels.cs`, `Views/SettingsView.xaml`, affected ChatView activity template; modify CLI `AgentCommands.Chat.RunState.cs`; add GUI/CLI projection/settings tests.

**Interfaces:** A bounded tree projection consumes Task 8 snapshots/events, keyed by stable agent ID and attempt ID; exposes role/task/actual model/status/deadline/usage/result/apply outcome. No role settings remain; model changes require explicit user prompts through Task 5.

- [x] Test out-of-order/replayed events, reopened root, parent Stop-all, refused child approvals/mutations, unapplied vs applied/conflict, terminal root export-only behavior and missing usage. Child sessions never appear in Recents.
- [x] Test accessible names, removed role settings, exact inherited defaults and read-only surfaces; keep rendering/projection separate from orchestration, avoid expanding ChatView/AgentRunController state machines.
- [x] Run GUI, CLI and Client focused projects before/after implementation. Native visual acceptance is manual unless explicitly authorized; do not control the user's desktop.

### Task 10: Documentation, full integration and evaluation

**Files:** Update `CONTEXT.md`, `docs/architecture.md`, `docs/operations/agent-tools.md`, `docs/operations/rpc.md`, `sdk/ru-agent/README.md`, `CHANGELOG.md`, affected route/count docs and an ADR for durable child ownership/delegated authority. Update this plan with completed boxes and evidence paths.

- [x] Add scripted end-to-end root coding+review/follow-up/apply scenario and failure scenarios (four parked roots, approval/cancel, crash/apply recovery); require both roles, not a review-only substitute.
- [x] Run `scripts/test_dotnet.ps1 -Configuration Debug`, then `.venv/Scripts/python.exe -m pytest tests -q`, then SDK typecheck/test/build sequentially. Preserve failures; investigate before retry. `git diff --check` must pass.
- [x] Report deterministic results separately from native UI and real-provider runs. Evaluate matched tasks/model/runtime for completion, unattended completion, time, aggregate usage, approvals and conflicts; never claim gains from fixtures alone. Real-provider tests require available configured credentials; do not invent successful evidence if unavailable.
- [x] Review affected documents; replace stale counts/claims and repair references, without deleting still-valid historical ADRs or research. Mark design implemented only once acceptance is met. Keep exposure disabled if a required safety gate fails.
- [x] Review the whole diff for scope, secret leakage, cancellation and permission bypass. Prepare a concise completion report with checks not run and remaining limitations; no commit/push without authorization.

## Plan review and execution

Approved for native execution in this session on 2026-10-17. Implementation is
complete through the client surfaces and final verification. Fresh Luna review
findings were reproduced and fixed; the final live pair met the strict both-role
completion gate. Evidence is indexed in `docs/operations/verification.md`.
Task 7's checkpoint describes the internal schema-v27 gate; schema v28 and current
exposure must pass the final Task 10 matrix independently.

The executor owns edits inline. A fresh Luna reviewer examines the complete diff
at the final gate, per repository instructions. Heavy process/loopback checks run
sequentially; findings require reproduction and focused regression checks.
