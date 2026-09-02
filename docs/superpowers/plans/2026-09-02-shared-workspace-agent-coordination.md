# Shared Workspace Agent Coordination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Implementation status (2026-09-02):** The protocol, durable coordinator,
> hash-authoritative external detection, encrypted journal, bounded merge,
> hash-guarded restore, startup reconciliation, authenticated activity routes,
> WinUI activity surface, and deterministic detection gate are implemented in
> this worktree. The deterministic gate deliberately does not impersonate or
> identify Codex/Opencode processes; real external identity remains
> `unknown_external` and live multi-process acceptance is a manual check.

**Goal:** Cho phép nhiều Ru Agent chạy trong cùng một physical workspace, đồng thời phát hiện và xử lý thay đổi từ Codex/Opencode hoặc process ngoài mà không làm mất dữ liệu.

**Architecture:** Thêm một coordinator durable theo `workspace_id`. Coordinator cấp `run lease`, lưu claim theo path, chụp snapshot trước/sau, ghi journal mã hóa và quyết định merge/reject theo policy. Shared mode là opt-in; exclusive mode và API hiện tại vẫn là mặc định tương thích ngược. Text chỉ được merge 3-way; binary conflict luôn fail-closed. External writer không cần nhận diện danh tính, chỉ cần phát hiện là `unknown external writer`.

**Tech Stack:** C#/.NET 8, ASP.NET Core minimal APIs, SQLite persistence, WinUI 3, existing `AgentV1` contracts/tools, xUnit.

---

## Task 1: Add protocol contracts and workspace concurrency settings

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/WorkspaceContracts.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/AgentApiContracts.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/TerminalContracts.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs`
- Add tests: `backend-dotnet/RuAgent.Protocol.Tests/Harness/WorkspaceConcurrencyContractTests.cs`

- [x] **1.1** Add `WorkspaceConcurrencyMode` (`Exclusive`, `Shared`), `WorkspaceConcurrencyPolicy`, `WorkspaceActivityItem`, `WorkspaceChangeItem`, `WorkspaceChangeRestoreRequest`, `WorkspaceWriteSet`, and `WorkspaceClaimState` records/enums. Keep JSON names snake_case and nullable fields explicit so old clients can deserialize responses.
- [x] **1.2** Add request/response DTOs for `GET /workspaces/{workspaceId}/activity`, `GET /workspaces/{workspaceId}/changes`, `POST /workspaces/{workspaceId}/changes/{changeId}/restore`, and `PUT /workspaces/{workspaceId}/concurrency`.
- [x] **1.3** Extend terminal tool input with optional `read_only` and `write_set` without changing required fields. `read_only=true` must imply an empty write set; a non-empty write set with `read_only=true` is rejected by validation.
- [x] **1.4** Register the exact terminal schema in `AgentToolDefinitionCatalog` and preserve the existing tool name and required argument behavior.
- [x] **1.5** Add contract tests for round-trip JSON, omitted optional fields, invalid read-only/write-set combinations, and backward-compatible terminal payloads.
- [x] **1.6** Run:
  ```powershell
  dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceConcurrencyContractTests
  ```
  Expected: all new contract tests pass.

## Task 2: Create durable coordinator persistence

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteSchema.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteQueries.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteWriteSet.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/SqliteHarnessEventStore.cs`
- Add: `backend-dotnet/RuAgent.Harness.Persistence/WorkspaceCoordinatorStore.cs`
- Add tests: `backend-dotnet/RuAgent.Harness.Persistence.Tests/WorkspaceCoordinatorStoreTests.cs`
- Modify tests: `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessSqliteSchemaTests.cs`

- [x] **2.1** Add migrations for `workspace_coordinators`, `workspace_run_members`, `workspace_path_claims`, `workspace_file_snapshots`, `workspace_change_journal`, and `workspace_mutations`. Store hashes, monotonic sequence, lease expiry, policy version, encrypted journal payload, and recovery state as durable columns.
- [x] **2.2** Keep the current unique active-workspace index during the migration. A named schema v5 final-gate migration drops only that index after coordinator tables are present.
- [x] **2.3** Implement `WorkspaceCoordinatorStore` methods for load/create coordinator, acquire/renew/release run membership, upsert/release path claims, snapshot files, append journal entries, allocate the monotonic commit sequence, list activity/changes, and mark restored changes.
- [x] **2.4** Make lease acquisition and sequence allocation transactional with `BEGIN IMMEDIATE` semantics. A failed transaction must leave no partial member, claim, or mutation row.
- [x] **2.5** Reuse existing SQLite connection, retry, and write-set helpers; do not introduce a second database or a second transaction abstraction.
- [ ] **2.6** Add tests for schema creation on a fresh DB, idempotent migration, exactly-one sequence allocation under parallel requests, lease expiry, and rollback after an injected write failure.
- [x] **2.7** Run:
  ```powershell
  dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceCoordinatorStoreTests
  ```
  Expected: all persistence tests pass and the schema validator reports no unknown or missing columns.

## Task 3: Implement claims, leases, and local/external change detection

**Files:**
- Add: `backend-dotnet/RuAgent.Harness.Core/WorkspaceConcurrency.cs`
- Add: `backend-dotnet/RuBackend/Harness/WorkspaceCoordinatorService.cs`
- Add: `backend-dotnet/RuBackend/Harness/WorkspaceExternalChangeMonitor.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Reconciliation/HarnessRecoveryScanner.cs`
- Add tests: `backend-dotnet/RuBackend.Tests/Harness/WorkspaceCoordinatorServiceTests.cs`
- Add tests: `backend-dotnet/RuBackend.Tests/Harness/WorkspaceExternalChangeMonitorTests.cs`
- Add tests: `backend-dotnet/RuAgent.Harness.Core.Tests/WorkspaceConcurrencyTests.cs`

- [x] **3.1** Implement a pure decision layer in `WorkspaceConcurrency.cs` for claim overlap, lease freshness, text/binary classification, sequence ordering, and conflict state transitions. The layer must be deterministic and contain no file-system or database calls.
- [ ] **3.2** Implement per-workspace heartbeat renewal and per-path claims. In exclusive mode, preserve the existing `workspace_busy` response. In shared mode, allow disjoint claims and reject overlapping binary claims before tool execution.
- [x] **3.3** Add `FileSystemWatcher`-based early warning with a debounced queue; pair every warning with authoritative pre/post hashes before emitting a final event. Watcher events alone must never mutate run state.
- [x] **3.4** Detect unknown external writers by comparing expected hashes and journal state. Emit `workspace_external_change_detected` with path, before/observed hash, run sequence, and `writer_kind=unknown_external`.
- [ ] **3.5** Emit Ru peer events from claims/heartbeats (`workspace_peer_claimed`, `workspace_file_changed_by_peer`) and mark stale claims as `workspace_claim_orphaned` during recovery.
- [x] **3.6** Ensure startup recovery expires active memberships/claims and rejects abandoned prepared journal rows idempotently. Keep the previously added `WaitingWorkspaceTrust` timeout behavior intact.
- [ ] **3.7** Add tests for disjoint claims, overlapping claims, heartbeat timeout, watcher debounce, unknown external edits, orphan cleanup, and duplicate event suppression.
- [ ] **3.8** Run:
  ```powershell
  dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceCoordinatorServiceTests|FullyQualifiedName~WorkspaceExternalChangeMonitorTests
  ```
  Expected: all detection and lease tests pass.

## Task 4: Add text merge, binary rejection, journal encryption, and restore

**Files:**
- Add: `backend-dotnet/RuAgent.Harness.Persistence/WorkspaceMergeEngine.cs`
- Add: `backend-dotnet/RuAgent.Harness.Persistence/WorkspaceJournalProtector.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/WorkspaceCoordinatorStore.cs`
- Add tests: `backend-dotnet/RuAgent.Harness.Persistence.Tests/WorkspaceMergeEngineTests.cs`
- Add tests: `backend-dotnet/RuAgent.Harness.Persistence.Tests/WorkspaceJournalProtectorTests.cs`

- [x] **4.1** Implement a bounded text-only 3-way merge using base/agent/external snapshots. Preserve UTF-8 and UTF-16 BOMs, normalize line endings only for comparison, and refuse files above the configured size limit.
- [ ] **4.2** Resolve overlapping text hunks with last-writer-wins using the coordinator's monotonic commit sequence, never wall-clock timestamps. Record both discarded content hashes in the journal.
- [x] **4.3** Classify unknown/binary data by content sniffing plus extension policy. Any binary conflict returns `concurrent_binary_conflict` and leaves the working file untouched.
- [x] **4.4** Protect journal payloads with the existing Windows credential/key-protection facility; store nonce, key version, and authentication tag beside ciphertext. Reading a journal with an unavailable key must fail closed and surface `journal_unavailable`.
- [x] **4.5** Implement restore as an authenticated, hash-guarded, retry-safe mutation that verifies the current hash before replacing the file and creates a new journal entry for the restore.
- [ ] **4.6** Test clean merge, non-overlapping merge, overlapping last-writer-wins, encoding/line-ending preservation, binary rejection, tamper detection, unavailable-key behavior, stale restore rejection, and successful restore.
- [x] **4.7** Run:
  ```powershell
  dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceMergeEngineTests|FullyQualifiedName~WorkspaceJournalProtectorTests
  ```
  Expected: all merge and journal tests pass.

## Task 5: Integrate coordination into run creation and terminal execution

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Service.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`
- Add tests: `backend-dotnet/RuBackend.Tests/Harness/SharedWorkspaceRunTests.cs`
- Add tests: `backend-dotnet/RuAgent.Broker.Tests/Tools/TerminalWriteSetTests.cs`

- [ ] **5.1** Inject `WorkspaceCoordinatorService` into the run creation path. Preserve `workspace_busy` in exclusive mode and return a stable conflict payload when shared-mode claims cannot be acquired.
- [ ] **5.2** Require every terminal invocation to declare `read_only` or a normalized write set. For legacy calls, derive a conservative write set from the run's approved workspace scope and emit a compatibility event.
- [ ] **5.3** Capture pre-execution hashes, execute through the existing terminal broker, then capture post-execution hashes before releasing claims. On cancellation or crash, leave a recoverable journal record.
- [ ] **5.4** Auto-include undeclared text changes in the current workspace, emit `workspace_undeclared_text_discovered`, and continue through merge policy. Reject undeclared binary changes with `concurrent_binary_conflict`.
- [ ] **5.5** Assign one coordinator commit sequence per successful mutation, use it for merge ordering, and make retries idempotent by mutation key.
- [ ] **5.6** Add tests proving two shared runs with disjoint files succeed, overlapping text changes resolve deterministically, overlapping binary changes fail without partial writes, terminal changes outside the declared write set follow policy, and legacy exclusive runs remain unchanged.
- [ ] **5.7** Run:
  ```powershell
  dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SharedWorkspaceRunTests
  dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --no-restore --filter FullyQualifiedName~TerminalWriteSetTests
  ```
  Expected: both focused suites pass.

## Task 6: Expose authenticated workspace activity and WinUI controls

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Modify: `gui-winui3/Services/ApiService.cs`
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `gui-winui3/Views/SettingsView.xaml`
- Modify: `gui-winui3/Views/SettingsView.xaml.cs`
- Modify: `gui-winui3/Views/ChatView.xaml`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Add tests: `backend-dotnet/RuBackend.Tests/Harness/WorkspaceActivityEndpointTests.cs`
- Add tests: `gui-winui3.Tests/WorkspaceActivityViewModelTests.cs`

- [x] **6.1** Add the four authenticated endpoints with the same auth, request-id, and error-envelope middleware used by existing `AgentV1` routes. Return workspace-scoped data through the existing AgentV1 route boundary.
- [x] **6.2** Add `ApiService` methods and DTO mapping for concurrency mode, activity, changes, and restore. Unknown event kinds remain displayable informational items.
- [ ] **6.3** Add a Shared workspace toggle in the existing `gui-winui3/Views/SettingsView.xaml` settings surface, persist it through `SettingsView.xaml.cs`, and show the current mode plus claim count.
- [x] **6.4** Add a compact activity/conflict panel in `gui-winui3/Views/ChatView.xaml` and its code-behind. Display Ru peers, unknown external writers, affected paths, sequence, and a guarded restore action for the latest journal entry.
- [x] **6.5** Keep UI updates non-blocking and capacity-bounded so activity rendering cannot delay voice playback or active chat streaming.
- [ ] **6.6** Add endpoint tests for auth denial, workspace isolation, pagination, restore authorization, and stable error codes. Add WinUI tests for unknown events, binary-conflict rendering, and disabled restore while a run is active.
- [ ] **6.7** Run:
  ```powershell
  dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceActivityEndpointTests
  dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceActivityViewModelTests
  ```
  Expected: both focused suites pass.

## Task 7: Recovery, migration gate, and exclusive-mode compatibility

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/Reconciliation/HarnessRecoveryScanner.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteSchemaValidator.cs`
- Add tests: `backend-dotnet/RuBackend.Tests/Harness/WorkspaceRecoveryTests.cs`
- Modify tests: `backend-dotnet/RuBackend.Tests/Harness/HarnessRecoveryScannerTests.cs`

- [ ] **7.1** Add startup reconciliation that scans expired coordinator leases, orphaned claims, incomplete journal entries, and mutations left in `prepared` state. Reconcile each record idempotently and emit a recovery event.
- [ ] **7.2** Add a migration readiness check that refuses shared mode when any required table/index/key version is missing, while allowing exclusive mode to continue during rollout.
- [ ] **7.3** Enable the unique active-workspace index drop only behind an explicit schema version and a passing compatibility probe. The probe must verify old client run creation, exclusive `workspace_busy`, and shared disjoint claims.
- [ ] **7.4** Verify restart recovery with a temp SQLite DB and temp workspace: kill/reopen between pre-snapshot, terminal completion, merge, and restore phases; assert no duplicate commit sequence and no orphaned claim.
- [ ] **7.5** Run:
  ```powershell
  dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceRecoveryTests|FullyQualifiedName~HarnessRecoveryScannerTests
  ```
  Expected: recovery tests pass and legacy recovery assertions remain green.

## Task 8: End-to-end detection and fault-injection verification

**Files:**
- Add: `tests/test_shared_workspace_detection.py`
- Add: `scripts/check_shared_workspace_detection.py`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/AgentV1EndpointTests.cs`
- Modify: `gui-winui3.Tests/AgentSessionRecoveryTests.cs`
- Add fixture: `tests/fixtures/shared-workspace-scenarios.json`

- [x] **8.1** Define deterministic scenarios for Ru+Ru peers, Ru+Codex, Ru+Opencode, terminal writes, editor writes, text overlap, binary overlap, undeclared files, process crash, and restart recovery. External tools are represented as real file mutations; identity remains `unknown_external`.
- [x] **8.2** Implement the deterministic Python filesystem harness and fixture gate. Live backend/process orchestration remains a manual acceptance step because external process identity is intentionally not inferred.
- [ ] **8.3** Add fault injection hooks for failure after snapshot, after terminal completion, after journal append, and during restore. Verify retries do not duplicate mutations and recovery releases claims.
- [ ] **8.4** Add a bounded stress scenario with two shared runs and one external writer. Assert monotonic sequences, no lost binary data, and eventual activity convergence.
- [x] **8.5** Run sequentially (not in parallel):
  ```powershell
  .venv\Scripts\python.exe -m pytest tests/test_shared_workspace_detection.py -q
  .venv\Scripts\python.exe scripts/check_shared_workspace_detection.py --fixture tests/fixtures/shared-workspace-scenarios.json
  ```
  Expected: all scenarios pass; the checker prints a machine-readable summary with zero failed gates.

## Task 9: Update operational documentation and complete the rollout gate

**Files:**
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/architecture.md`
- Modify: `backend-dotnet/README.md`
- Modify: `README.md`
- Add: `docs/operations/shared-workspace-coordination.md`
- Add: `docs/adr/0008-shared-workspace-coordination.md`

- [x] **9.1** Document the exclusive/shared modes, claim rules, terminal `read_only`/`write_set`, text merge semantics, binary fail-closed behavior, unknown external writer detection, journal retention, restore flow, and troubleshooting event codes.
- [x] **9.2** Add an ADR recording why OS-level sandboxing is out of scope for this feature, why external identity is intentionally unknown, and why monotonic coordinator sequence is used instead of wall-clock time.
- [x] **9.3** Add a manual acceptance checklist: Ru Agent + editor, Ru Agent + Codex, Ru Agent + Opencode, two Ru Agents with disjoint files, overlapping text, overlapping binary, crash/restart, and restore. Include expected UI/activity evidence for each.
- [x] **9.4** Scan docs for stale claims about one-agent-per-workspace and update or remove only claims contradicted by the shipped behavior. No HTML report was created.
- [x] **9.5** Run the final verification sequence:
  ```powershell
  git diff --check
  dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj --no-restore
  dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj --no-restore
  dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
  dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --no-restore
  dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
  .venv\Scripts\python.exe -m pytest tests/test_shared_workspace_detection.py -q
  ```
  Run heavyweight suites sequentially. Expected: all suites pass, `git diff --check` is clean, and the manual acceptance checklist has recorded evidence.
- [ ] **9.6** Before any commit, inspect `git status --short`, stage only files owned by this plan, and leave the user's unrelated dirty changes untouched.

## Completion gate

The implementation is complete only when all of the following are true:

- Shared mode is opt-in and exclusive mode remains backward compatible.
- Ru peer claims and unknown external changes are both visible in activity APIs/UI.
- Text conflicts resolve deterministically by coordinator sequence; binary conflicts never overwrite silently.
- Terminal writes are governed by `read_only`/`write_set`, with undeclared text auto-included and undeclared binary rejected.
- Journal entries are encrypted, tamper-detected, restorable, and recovered after restart/crash.
- Focused, full, and manual detection gates pass, with no regression to voice/chat responsiveness.
- Documentation matches the shipped routes, tools, and operational behavior; no HTML report is generated.
