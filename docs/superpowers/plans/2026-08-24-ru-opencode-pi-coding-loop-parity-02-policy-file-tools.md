# Ru Coding-Loop Parity Phase 2: Policy Authority and Complete File Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Broker-enforced Standard/Full Access snapshots, durable workspace trust, granular approvals/rules, and the complete safe `files.*` contract available to the shared headless Agent loop.

**Architecture:** Public Host builds immutable encrypted-at-rest policy snapshots from preset/global/project/run rules; Broker v2 binds snapshot and canonical workspace identity to the run lease, resolves every intent, makes the authoritative decision, and executes focused file adapters; Core owns approval waiting and durable event order but never duplicates policy evaluation.

**Tech Stack:** .NET 10, xUnit, ASP.NET Core, SQLite WAL, AES-GCM/DPAPI, Broker WebSocket protocol v2, SHA-256, Windows final-path/file-identity APIs, atomic same-volume replacement.

---

## Checkpoint outcome

At the end of this phase:

- Standard automatically allows ordinary trusted-workspace reads/creates/edits and asks for delete, protected/outside paths, declared network, and unknown operations.
- Full Access changes fallback decisions, but never bypasses workspace trust or protocol invariants and remains a current-Windows-user boundary.
- Allow once/run/project and Deny are durable user interactions; rule/snapshot replacement follows the approved old-snapshot execution ordering.
- `files.list`, `files.glob`, `files.search`, offset/limit `files.read`, `files.write`, `files.edit`, `files.apply_patch`, `files.delete`, and one-time `files.restore` work through Broker.
- Every mutation has preimage checks, atomic replacement where supported, hashes, bounded diff summary, run attribution, and recoverable delete semantics.
- CLI exposes the approved permission/policy actions; Desktop still uses Agent v1 until phase 4.

Terminal/Git remain unavailable in Agent v2 at this checkpoint.

## Task 1: Add immutable policy and workspace contracts

**Files:**

- Modify `backend-dotnet/RuAgent.Harness.Core/Policy/PolicyRule.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/Policy/PolicyMatcher.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/Policy/PolicySnapshot.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/Policy/PolicySnapshotCanonicalizer.cs` for the complete matcher set.
- Modify `backend-dotnet/RuAgent.Protocol/Harness/BrokerPolicyContracts.cs`.
- Create `backend-dotnet/RuAgent.Protocol/Harness/WorkspaceContracts.cs` only for API fields not already present in `BrokerRunLeaseContracts.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core.Tests/PolicySnapshotCanonicalizerTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/PolicySnapshotImmutabilityTests.cs`.

- [ ] Write RED tests for the exact immutable snapshot fields:

```csharp
public sealed record PolicySnapshot(
    long Version,
    AgentPermissionPreset Preset,
    WorkspaceIdentity Workspace,
    IReadOnlyList<PolicyRule> OrderedRules,
    DateTimeOffset CreatedAt,
    string Sha256);
```

`PolicyRule` contains stable ID, `Global|Project|Run|Once` scope, decision, matcher, creation order, and optional expiry. `PolicyMatcher` has optional tool ID/version, canonical path pattern, resolved executable, argv pattern, cwd pattern, operation class, and network intent.

- [ ] Add RED canonicalization tests: dictionaries/property order do not change hash; rule order, decision, path, argv, workspace identity, preset, or version does; invalid/unknown matcher kinds fail closed rather than being omitted.
- [ ] Add RED immutability tests proving a session preset change after run creation cannot change that run's snapshot and Full Access is not inherited by a new session.
- [ ] Extend the phase-1 workspace tests to prove snapshot construction uses only an already trusted exact identity and a moved identity cannot inherit project rules.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~PolicySnapshot|FullyQualifiedName~WorkspaceTrust"
```

Expected RED: policy/workspace types absent.

- [ ] Implement canonical UTF-8 JSON with explicitly ordered fields and SHA-256 lowercase hex. Do not use default object serialization as the canonical hash contract.
- [ ] Keep path/argv matcher values private payload references in persistence-facing records; only the active decrypted snapshot contains matcher text.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): define immutable policy snapshots`.

## Task 2: Persist encrypted rules, workspace trust, and run-rule expiry

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Persistence/Policy/SqlitePolicyRuleStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Policy/PolicySnapshotLoader.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Persistence/Workspace/SqliteWorkspaceStore.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/PolicyRuleStoreTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/PolicySnapshotLoaderTests.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Persistence.Tests/WorkspaceStoreTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/RunRuleExpiryTests.cs`.

- [ ] Add RED rule-store tests for explicit global/project/run ownership; exact-by-default approval matchers; encrypted path/argv/cwd values; stable metadata remaining queryable; invalid scope/decision/matcher rejection.
- [ ] Add RED ordering tests: built-in preset fallback, global rules, project rules, run rules, once decision; last matching rule wins within the canonical ordered list.
- [ ] Add RED lifecycle tests: run rule deleted in the same transaction as terminal transition; project/global rules survive session/run deletion; session-only data and encrypted payloads are removed when session deletion completes.
- [ ] Add RED workspace tests: store canonical root only in encrypted payload, store volume/root file identity and display hash, one active trusted identity per workspace ID, and moved/root-identity mismatch creates a fresh trust request.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~Policy|FullyQualifiedName~Workspace|FullyQualifiedName~RunRule"
```

Expected RED: stores/loaders absent.

- [ ] Implement all writes through the existing sole-writer SQLite services. Matcher payload writes and rule metadata writes are atomic.
- [ ] Snapshot loader decrypts only active-run rule values, validates every matcher, computes a new version/hash, and returns no partial snapshot on any invalid/decryption failure.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): persist workspace trust and rules`.

## Task 3: Bind Broker path classification to the trusted workspace identity

**Files:**

- Modify `backend-dotnet/RuBackend/Harness/Workspace/WindowsWorkspaceIdentityResolver.cs` only for the v2 lease DTO mapping.
- Modify `backend-dotnet/RuBackend.Tests/Harness/WorkspaceIdentityResolverTests.cs` with lease-mapping regression coverage.
- Create `backend-dotnet/RuAgent.Broker/Tools/Paths/CanonicalPathResolver.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Paths/ResolvedPath.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Paths/ProtectedPathCatalog.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/CanonicalPathResolverTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/ProtectedPathCatalogTests.cs`.
- Retire `backend-dotnet/RuAgent.Broker/Tools/WorkspacePathGuard.cs` only after all callers use the new resolver.

- [ ] Extend RED host tests to prove the exact phase-1 trusted identity/root/hash is mapped into the Broker v2 lease without reinterpreting repository markers or current directory.
- [ ] Retain the phase-1 trust endpoint suite as a regression gate; add project-rule isolation after workspace identity movement.
- [ ] Write RED Broker path tests for relative-inside, explicit absolute-inside, explicit absolute-outside, protected, missing-create target, junction/symlink escape, and re-resolution after link replacement.
- [ ] Define the protected catalog in tests: Agent Harness database/key/descriptor/rule locations, Ru credential locations, `.git/**`, and `.env*`; version the catalog as `ProtectedPathCatalog.Version = "1"`.
- [ ] Run RED sequentially:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~WorkspaceIdentity|FullyQualifiedName~WorkspaceTrust"
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~CanonicalPathResolver|FullyQualifiedName~ProtectedPathCatalog"
```

Expected RED: resolver/trust services absent.

- [ ] Reuse the phase-1 final-path workspace identity and implement Broker target resolution. Return typed `InsideWorkspace|OutsideWorkspace|Protected`; do not reject outside/protected before policy can return `Ask`.
- [ ] Require relative paths to anchor to workspace. Outside access requires an explicit absolute input. Re-resolve at evaluation, preparation, and execution.
- [ ] Run GREEN. Expected: zero failures, including adversarial link tests supported by the test account.
- [ ] Commit: `feat(agent): bind trust to canonical workspace identity`.

## Task 4: Extend Broker v2 with complete authoritative rule evaluation

**Files:**

- Modify `backend-dotnet/RuAgent.Protocol/Harness/BrokerRunLeaseContracts.cs`.
- Modify `backend-dotnet/RuAgent.Protocol/Harness/BrokerPolicyContracts.cs`.
- Modify `backend-dotnet/RuAgent.Protocol/AgentEnvelopeCodec.cs`.
- Modify `backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/PolicyContext.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/PolicyContextFactory.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/PolicyRuleMatcher.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Runtime/BrokerRunLease.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/AgentEnvelopeTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs`.

- [ ] Extend the phase-1 compatibility tests: v1 hello/session/tool flow remains valid; v1 rejects v2-only messages; v2 `run.open` now carries the complete immutable ordered-rule snapshot and still requires a matching hash before capabilities.
- [ ] Define v2 messages in tests: `run.open`, `run.policy.rebind`, `run.close`, `tool.evaluate`, `tool.decision`, `tool.prepare`, `tool.prepared`, `tool.execute`, `tool.result`, `approval.respond`, `approval.result`, and existing cancel. `tool.output.delta` is reserved and implemented in phase 3.
- [ ] Add RED evaluator tests proving protocol invariants run first; then last-match rule evaluation; Standard fallbacks; Full Access fallbacks; malformed/unknown patterns deny; model-supplied safety labels have no effect.
- [ ] Add RED lease tests: Broker path root comes only from the v2 lease, not `RU_WORKSPACE_ROOT`/cwd; moved workspace or hash mismatch fails closed; rebind increments version and invalidates pending decisions under the older snapshot.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentEnvelope|FullyQualifiedName~Policy|FullyQualifiedName~BrokerSession|FullyQualifiedName~BrokerHost"
```

Expected RED: v2 messages and policy evaluator absent.

- [ ] Extend the phase-1 dual-version parsing without silently treating a v2 payload as v1. Keep v1 default call sites unchanged until phase 4.
- [ ] Split `ToolExecutionService` responsibilities so it coordinates schema → context → decision → preparation → dispatch; policy matching and canonical resolution live in focused classes, not another expansion of the current large file.
- [ ] Implement Standard built-ins exactly from the design and Full Access `Allow` fallback subject to non-overridable invariants. Mark test/build command risk for phase 3 analyzers; no terminal capability is advertised yet.
- [ ] Run GREEN. Expected: zero failures and v1 Broker integration still green.
- [ ] Commit: `feat(broker): bind policy snapshots to run leases`.

## Task 5: Make approval and snapshot replacement ordering durable

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Core/Approval/ApprovalCommand.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Approval/ApprovalState.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`.
- Create `backend-dotnet/RuBackend/Harness/Policy/HarnessPolicySnapshotService.cs`.
- Create `backend-dotnet/RuBackend/Harness/Policy/HarnessApprovalService.cs`.
- Modify `backend-dotnet/RuBackend/Harness/AgentV1Service.cs`.
- Modify `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`.
- Refactor `backend-dotnet/RuAgent.Broker/Policy/PendingApprovalStore.cs` and `ApprovalTokenService.cs` for v2 decision tokens.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/ApprovalWorkflowTests.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalStoreTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ApprovalTokenServiceTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentApprovalWorkflowTests.cs`.

- [ ] Add RED Core tests for `AllowOnce`, `AllowRun`, `AllowProject`, and `Deny`; approval waiting consumes no budget; terminal/stale approval has no effect; 30-minute expiry re-evaluates and presents a new approval rather than executing.
- [ ] Add RED binding tests for run ID, tool ID/version, canonical path/executable/argv/cwd/operation/network, snapshot version, intent hash, expiry, and one-time consumption. Any changed field returns `approval_stale`.
- [ ] Add RED exact-scope preview tests: Broker returns the normalized candidate matcher; run/project approval UI/CLI sees it; approval-derived rules cannot broaden to a glob.
- [ ] Add the critical RED ordering test:

```text
consume one-time approval under snapshot N
-> persist reusable rule (or record rule_save_failed)
-> execute the already-authorized call under snapshot N
-> persist final receipt
-> build/bind snapshot N+1
-> invalidate other pending decisions from N
```

Assert the initiating call remains valid and no other pending call executes.

- [ ] Add RED failure tests: rule persistence failure still permits once-only execution and reports `reusable_rule_not_saved`; Broker restart before dispatch invalidates ephemeral decision and causes safe re-evaluation, never adapter invocation.
- [ ] Run RED sequentially across Core, Broker, Backend focused suites.
- [ ] Implement two-phase v2 approval: `approval.respond` returns an authorization token and never invokes the adapter; Core commits `tool.execution_requested` before `tool.execute`.
- [ ] Run GREEN. Expected: zero failures and exact ordering assertions pass.
- [ ] Commit: `feat(agent): persist granular approval decisions`.

## Task 6: Implement bounded file discovery and reads

**Files:**

- Modify `backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileToolLimits.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileListAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileGlobAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileSearchAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileReadAdapter.cs`.
- Keep `backend-dotnet/RuAgent.Broker/Tools/Desktop/FileTools.cs` as a thin compatibility facade, then remove it when no v1 caller remains in phase 4.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/FileReadAdapterTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/FileDiscoveryAdapterTests.cs`.

- [ ] Add RED schema tests for exact IDs/fields: `files.list(path=".", max_entries)`, `files.glob(path=".", pattern, max_results)`, `files.search(path=".", query, file_glob?, case_sensitive, max_results)`, and `files.read(path, offset, limit)`.
- [ ] Add RED behavior tests for deterministic ordinal relative paths, hidden/binary handling, inaccessible entry skip metadata, junction/symlink containment, result/byte/time caps, offset beyond EOF, UTF-8 boundary behavior, and bounded `truncated` metadata.
- [ ] Use large/sparse files to prove no adapter allocates complete untrusted file size and search drains/truncates without unbounded result accumulation.
- [ ] Add policy tests: trusted-workspace discovery/read allows in Standard; protected/outside asks; Full Access allows after trust; malformed paths deny before adapter.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ToolCatalog|FullyQualifiedName~FileRead|FullyQualifiedName~FileDiscovery"
```

Expected RED: missing schemas/adapters.

- [ ] Implement streaming enumeration/search/read with fixed caps. Do not shell out to `rg` from file adapters; terminal analyzers own command execution in phase 3.
- [ ] Return typed relative paths plus counts/truncation only in safe receipt fields; matching line text is a private payload.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): add bounded file discovery tools`.

## Task 7: Implement hash-guarded atomic writes, edits, and patches

**Files:**

- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileHashService.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/AtomicFileReplacement.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileMutationJournal.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileWriteAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileEditAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/UnifiedPatchParser.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FilePatchAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/FileMutationAdapterTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/FilePatchAdapterTests.cs`.
- Create `backend-dotnet/RuBackend/Harness/Workspace/RunChangeSetStore.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/RunChangeSetStoreTests.cs`.

- [ ] Add RED schema tests. Every mutation requires `expected_sha256`: lowercase 64-hex for existing content or literal `missing` for create. `files.apply_patch` requires a bounded unified patch plus an exact expected-hash entry for every target.
- [ ] Add RED tests for preimage mismatch returning `workspace_changed` with zero write, concurrent external change, same-volume temp file, flush/atomic replacement, permission error, and link replacement between evaluation/preparation/execution.
- [ ] Add RED edit tests for exact occurrence count, 4 MiB input cap, UTF-8 validity, no match/multiple match, before/after hashes, and bounded diff summary stored privately.
- [ ] Add RED multi-file patch tests: parse and validate all targets before first replacement; failure on target N writes none; simulated replacement failure uses bounded journal to restore prior replacements; no path can repeat or escape.
- [ ] Add RED run-attribution tests: initial observed hash, mutation before/after hashes, final change set includes only paths changed by that run, and unrelated dirty files remain absent.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~FileMutation|FullyQualifiedName~FilePatch"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~RunChangeSet"
```

Expected RED: mutation services absent.

- [ ] Implement side-effect-free preparation and execution-time revalidation. Core persists preparation payloads plus `tool.execution_requested` before dispatch.
- [ ] Keep mutation orchestration in focused files; do not grow `FileTools.cs` into the new implementation.
- [ ] Run GREEN sequentially. Expected: zero failures.
- [ ] Commit: `feat(agent): add atomic hash guarded file mutations`.

## Task 8: Add recoverable delete and one-time restore

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Core/Recovery/FileRecoveryReference.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence/Recovery/FileRecoveryStore.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileDeleteAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Files/FileRestoreAdapter.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/FileDeleteRestoreTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Persistence.Tests/FileRecoveryStoreTests.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs` without adding file-specific branches.
- Modify `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs` to map generic recovery payload intents.

- [ ] Add RED tests for delete preparation reading at most the manifest recovery cap and returning a private payload intent before any delete; oversized file returns `recovery_too_large` and remains intact.
- [ ] Add RED transaction test: encrypted recovery payload, ownership metadata, and `tool.execution_requested` commit before Broker delete dispatch.
- [ ] Add RED delete tests for expected hash, execution-time re-resolution, opaque recovery ID receipt, missing/changed file, and no recovery bytes in safe events/diagnostics.
- [ ] Add RED restore tests: advertised only while active run owns valid recovery ID; original target must still be missing; recreated/changed target rejects; success consumes ID once; replay returns `recovery_consumed`; terminal run expires unconsumed run recovery according to retention policy.
- [ ] Include a binary byte-identity restore case whose content contains a known-secret sentinel: recovery remains encrypted/internal and restores exact bytes, while the same sentinel is redacted from every model/client/diagnostic projection.
- [ ] Run RED sequentially in Persistence and Broker focused suites.
- [ ] Implement generic preparation payload handling in Core; Host gateway maps recovery payload reference to Broker restore content without exposing it to model-visible output.
- [ ] Run GREEN. Expected: zero failures and delete is never irreversible within the declared cap.
- [ ] Commit: `feat(agent): add recoverable delete and restore`.

## Task 9: Activate approval/policy API and CLI commands

**Files:**

- Modify `backend-dotnet/RuBackend/Harness/AgentV1Service.cs` and endpoint DTOs.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentPolicyEndpointTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentApprovalEndpointTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentSessionPermissionEndpointTests.cs`.
- Modify `backend-dotnet/RuAgent.Cli/AgentCliParser.cs`.
- Modify `backend-dotnet/RuAgent.Cli/AgentCommands.cs`.
- Create `backend-dotnet/RuAgent.Cli/PolicyJsonCodec.cs`.
- Modify `backend-dotnet/RuAgent.Cli.Tests/AgentCliParserTests.cs`.
- Modify `backend-dotnet/RuAgent.Cli.Tests/ReadOnlyTracerTests.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/PolicyCommandTests.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/ApprovalCommandTests.cs`.
- Modify `backend-dotnet/RuAgent.Cli/README.md`.

- [ ] Add RED endpoint tests for approval first-writer wins, later concurrent response `approval_stale`, exact scope, policy list/create/delete, global rule interactive-origin requirement, and an approval card being unable to create a global rule.
- [ ] Add RED session-preset tests on the existing policy user-control route: `POST /agent/v1/policies` with kind `session_preset` changes one session and returns a monotonic selection version; Full requires interactive confirmation; `POST /agent/v1/runs` must match that stored preset/version; request-body preset alone cannot escalate Standard to Full.
- [ ] Add RED CLI tests for `approve --scope once|run|project`, `deny`, `policy list`, `policy import --global FILE`, `policy remove`, and `run --preset full` interactive warning.
- [ ] Prove `--json` cannot confirm Full Access, workspace trust, an approval, or global policy import; no switch combination bypasses those constraints.
- [ ] Add versioned policy JSON tests: reject unknown version/fields, normalize and display matchers/decision before confirmation, encrypt sensitive matcher values at rest.
- [ ] Run RED across Backend and CLI focused tests.
- [ ] Implement endpoints/commands and activate the phase-1 `phase_not_available` routes. Preset changes affect the next run only.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): expose policy and approval controls`.

## Task 10: Verify phase 2 and reconcile current docs

**Files:**

- Modify `README.md`, `backend-dotnet/README.md`, `docs/architecture.md`, `docs/operations/agent-tools.md`, `CONTEXT.md`, and `tests/test_current_documentation.py` for facts now implemented.
- Inspect `TODO.md`; retain the OS sandbox item and do not mark it complete.
- Modify `build.bat` only if new phase-2 sources/projects require build coverage.

- [ ] Search for stale/unsafe claims and placeholders:

```powershell
rg -n "Agent has no terminal|Full.*separate|network isolation|filesystem sandbox|TBD|TODO|FIXME|NotImplementedException" D:\LLM\README.md D:\LLM\backend-dotnet\README.md D:\LLM\CONTEXT.md D:\LLM\docs D:\LLM\backend-dotnet\RuAgent.Harness.Core D:\LLM\backend-dotnet\RuAgent.Harness.Persistence D:\LLM\backend-dotnet\RuAgent.Broker D:\LLM\backend-dotnet\RuBackend\Harness D:\LLM\backend-dotnet\RuAgent.Cli
```

Expected: terminal remains described as phase 3/not yet active; no policy control is called sandboxing; only explicit deferred/historical TODOs remain.

- [ ] Run phase suites sequentially:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_current_documentation.py
cmd /c D:\LLM\build.bat
```

Expected: all green, route count remains 54+2, production build succeeds.

- [ ] Run a deterministic temp-workspace matrix: trust, list/glob/search/read, create/write/edit/multi-file patch, preimage conflict, protected/outside approval, oversized delete refusal, delete/restore once, reconnect, and history dedupe. Do not touch the real `D:\LLM` worktree with mutation acceptance.
- [ ] Inspect current docs and delete only documents genuinely made obsolete; retain dated specs/plans as history.
- [ ] Inspect/stage only phase-2 files and commit: `feat(agent): complete policy and file tool slice`.
