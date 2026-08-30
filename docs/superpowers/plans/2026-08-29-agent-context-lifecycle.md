# Agent Context Lifecycle and Compaction Implementation Plan

> **Status:** Future implementation plan; work has not started. Keep this
> proposal separate from the current runtime contract until its gates are
> implemented and verified.

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Replace provider-limit hard stops with deterministic, durable context compaction while preserving audit history and fail-closed execution.

**Architecture:** Keep the existing immutable Core context assembler and SQLite event store. Add a pure budget/planner layer, an append-only compaction record plus model-only summary payload, and a coordinator operation that commits the summary, projection, event, and outbox atomically with the existing run CAS. Expose manual compaction through the Public Host and reuse it from the typed command router.

**Tech Stack:** C#/.NET 10, xUnit, Microsoft.Data.Sqlite, existing Harness Core/Persistence/Public Host contracts.

## Global Constraints

- Compaction is append-only; historical payloads and events are never deleted or rewritten.
- The normal automatic target is 62% of usable prompt budget and triggers at 80%; aggressive manual target is 48%.
- System instructions, memory, current user prompt, and unfinished tool/workflow turn are protected.
- A projection that still exceeds the usable budget never invokes the model.
- SQLite v2 databases migrate additively to v3 without losing rows.
- Summary text and raw covered tool/file content are never exposed in client-visible events.
- Deterministic tests use .venv\Scripts\python.exe -m pytest only for Python suites; .NET suites run sequentially with dotnet test.

---

### Task 1: Add the pure budget policy

**Files:**
- Create: backend-dotnet/RuAgent.Harness.Core/Context/ContextBudgetPolicy.cs
- Test: backend-dotnet/RuAgent.Harness.Core.Tests/ContextBudgetPolicyTests.cs

**Interfaces:**
- Consumes: IModelContextLimitProvider.MaxPromptTokens(RunId) and the generation output reservation supplied by the coordinator.
- Produces: ContextBudgetPolicyOptions, ContextCompactionStrategy, ContextBudgetDecision, and ContextBudgetPolicy.Evaluate(long estimatedTokens, long maxPromptTokens, ContextCompactionStrategy strategy).

- [ ] Step 1: Write the failing threshold tests

Add tests with these exact assertions:

    [Fact]
    public void Normal_policy_triggers_at_eighty_percent_and_targets_sixty_two_percent()
    {
        var policy = new ContextBudgetPolicy(new ContextBudgetPolicyOptions(100, 0));
        var decision = policy.Evaluate(161, 300, ContextCompactionStrategy.Normal);
        Assert.Equal(200, decision.UsablePromptTokens);
        Assert.Equal(160, decision.SoftTriggerTokens);
        Assert.Equal(124, decision.TargetTokens);
        Assert.True(decision.NeedsCompaction);
    }

    [Fact]
    public void Aggressive_policy_uses_the_lower_target_but_never_a_zero_budget()
    {
        var policy = new ContextBudgetPolicy(new ContextBudgetPolicyOptions(100, 0));
        var decision = policy.Evaluate(1, 50, ContextCompactionStrategy.Aggressive);
        Assert.Equal(1, decision.UsablePromptTokens);
        Assert.Equal(1, decision.TargetTokens);
        Assert.False(decision.NeedsCompaction);
    }

- [ ] Step 2: Run the focused tests and verify the expected API failure

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~ContextBudgetPolicyTests

Expected: compile failure because the new policy types do not exist.

- [ ] Step 3: Implement the minimal policy

Create the enum and records, with defaults OutputReserveTokens=256,
SafetyMarginTokens=64, NormalTriggerRatio=0.80, NormalTargetRatio=0.62, and
AggressiveTargetRatio=0.48. Clamp usable, trigger, and target to at least one;
reject negative reservations, ratios outside (0,1], and maxPromptTokens <= 0.
Set NeedsCompaction only when the estimate is greater than SoftTriggerTokens.

- [ ] Step 4: Run the focused tests and verify green

Run the same dotnet test command. Expected: both tests pass with no warnings.

- [ ] Step 5: Commit the pure policy

    git add backend-dotnet/RuAgent.Harness.Core/Context/ContextBudgetPolicy.cs backend-dotnet/RuAgent.Harness.Core.Tests/ContextBudgetPolicyTests.cs
    git commit -m "feat: add context budget policy"

---

### Task 2: Add summary entries and a deterministic compaction planner

**Files:**
- Modify: backend-dotnet/RuAgent.Harness.Core/Context/ModelContextEntry.cs
- Modify: backend-dotnet/RuAgent.Harness.Core/Context/ModelContextSnapshot.cs
- Create: backend-dotnet/RuAgent.Harness.Core/Context/ContextCompactionPlanner.cs
- Modify: backend-dotnet/RuAgent.Harness.Core/ContinuationSummaryBuilder.cs
- Test: backend-dotnet/RuAgent.Harness.Core.Tests/ContextCompactionPlannerTests.cs
- Test: backend-dotnet/RuAgent.Harness.Core.Tests/AgentContextAssemblerTests.cs

**Interfaces:**
- Consumes: ModelContextSnapshot, ContextBudgetDecision, and the current run's protected-entry predicate.
- Produces: ContextCompactionPlan with CompactionId, Strategy, Reason, CoveredRanges, SummaryText, BeforeTokens, AfterTokens, and InputHash.

- [ ] Step 1: Write failing planner tests

Create a snapshot containing a System entry, a memory User entry, three older
turn/tool entries, a current User entry, and an unfinished Assistant step. Assert
that a normal plan includes only eligible older entries, leaves every protected
entry in RemainingEntries, has a stable SHA-256 input hash, and produces identical
text on two calls. Assert that the summary entry uses a synthetic projection
sequence and ModelContextEntryKind.Summary.

- [ ] Step 2: Run the planner tests and verify the missing-type failure

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~ContextCompactionPlannerTests

Expected: compile failure for the missing summary kind/planner.

- [ ] Step 3: Implement the summary model

Add Summary to ModelContextEntryKind and a factory
ModelContextEntry.Summary(string compactionId, long projectionSequence, string text,
IReadOnlyList<ContextEntryRange> coveredRanges). Keep source/sequence validation
and cap summary text at 32 KiB. Serialize summary metadata in canonical JSON
without reusing a source sequence; update KindName and all switch expressions.
Add ContextEntryRange as a bounded source/start/end record.

- [ ] Step 4: Implement the deterministic planner

Select eligible entries oldest-first until the target estimate is met, retaining
system/base-memory entries, the latest user entry, and any assistant/tool group
whose workflow is incomplete. Use the existing ContinuationSummaryBuilder format,
but include bounded source ranges and outcome counts. Return no plan when the
estimate is at or below target or there are no eligible entries. Compute the input
hash over canonical covered entries plus strategy and reason. Reject a plan that
covers a protected entry.

- [ ] Step 5: Run planner and assembler tests

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter "FullyQualifiedName~ContextCompactionPlannerTests|FullyQualifiedName~AgentContextAssemblerTests"

Expected: all new and existing context tests pass.

- [ ] Step 6: Commit the planner

    git add backend-dotnet/RuAgent.Harness.Core/Context backend-dotnet/RuAgent.Harness.Core/ContinuationSummaryBuilder.cs backend-dotnet/RuAgent.Harness.Core.Tests/ContextCompactionPlannerTests.cs backend-dotnet/RuAgent.Harness.Core.Tests/AgentContextAssemblerTests.cs
    git commit -m "feat: add deterministic context compaction planner"

---

### Task 3: Extend payload and commit contracts

**Files:**
- Modify: backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessPayloadIntent.cs
- Modify: backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessCommit.cs
- Create: backend-dotnet/RuAgent.Harness.Core/Persistence/ContextCompactionIntent.cs
- Modify: backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessEventIntent.cs
- Test: backend-dotnet/RuAgent.Harness.Core.Tests/HarnessCommitContractTests.cs
- Test: backend-dotnet/RuAgent.Harness.Core.Tests/ModelContextLimitTests.cs

**Interfaces:**
- Consumes: ContextCompactionPlan and a model-only HarnessPayloadIntent.
- Produces: HarnessCommit.ContextCompaction with the compaction ID, covered ranges JSON, strategy, reason, estimates, input hash, and summary payload ID.

- [ ] Step 1: Write the failing contract tests

Assert that HarnessPayloadKind.ModelContextSummary accepts only
HarnessPayloadAudience.ModelContext and Utf8Text, rejects authenticated-client
audience, and that ContextCompactionIntent rejects an empty range list, a summary
payload ID mismatch, or an input hash that is not lowercase hexadecimal SHA-256.

- [ ] Step 2: Run the contract tests and verify failure

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~HarnessCommitContractTests

Expected: compile failure for the new enum/intent.

- [ ] Step 3: Implement the contract additions

Add ModelContextSummary and a policy entry with ModelContext audience and Utf8Text
encoding. Add the immutable ContextCompactionIntent record and an optional
constructor argument/property on HarnessCommit. Validate that the intent belongs
to the same run, references a payload in the same commit, and contains bounded
canonical range JSON. Add a safe HarnessEventKind.ContextCompacted value whose
payload is metadata-only.

- [ ] Step 4: Run the Core contract tests

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter "FullyQualifiedName~HarnessCommitContractTests|FullyQualifiedName~ModelContextLimitTests"

Expected: new contract tests pass; the old provider-limit assertions remain red
until Task 5 changes coordinator behavior.

- [ ] Step 5: Commit contract changes

    git add backend-dotnet/RuAgent.Harness.Core/Persistence backend-dotnet/RuAgent.Harness.Core.Tests/HarnessCommitContractTests.cs backend-dotnet/RuAgent.Harness.Core.Tests/ModelContextLimitTests.cs
    git commit -m "feat: add durable context compaction commit contract"

---

### Task 4: Migrate SQLite and reconstruct the active projection

**Files:**
- Modify: backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteSchema.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteSchemaValidator.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteQueries.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteWriteSet.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/SqliteHarnessEventStore.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/HarnessPayloadSqlite.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/Context/SqliteModelContextSource.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence/HarnessPayloadStore.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessSqliteSchemaTests.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessProjectionTransactionTests.cs
- Modify: backend-dotnet/RuAgent.Harness.Persistence.Tests/ModelContextSourceTests.cs

**Interfaces:**
- Consumes: HarnessCommit.ContextCompaction and ModelContextSummary payloads.
- Produces: schema v3, atomic compaction rows, and a SqliteModelContextSource snapshot with covered historical entries projected out and one synthetic summary inserted.

- [ ] Step 1: Write the migration and transaction tests

Add a v2 fixture that sets PRAGMA user_version=2, initializes the store, and
asserts version 3 plus the agent_context_compactions table/indexes while all v2
rows remain. Add a transaction test that commits a run update, summary payload,
compaction row, event, and outbox, then opens a second connection and asserts the
active projection; force a foreign-key failure and assert none of the five writes
remain. Add a restart test proving identical canonical context.

- [ ] Step 2: Run persistence tests and verify the new schema behavior fails

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj --filter "FullyQualifiedName~HarnessSqliteSchemaTests|FullyQualifiedName~HarnessProjectionTransactionTests|FullyQualifiedName~ModelContextSourceTests"

Expected: migration/table/projection assertions fail because schema is still v2 and writes are not handled.

- [ ] Step 3: Add the additive v3 migration

Set HarnessSqliteSchema.CurrentVersion = 3. Accept versions 0, 2, and 3; create
the compaction table/indexes in the v2 migration transaction and update
user_version. The table must contain compaction_id TEXT PRIMARY KEY, run_id TEXT
NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE, summary_payload_id
TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
expected_run_version INTEGER NOT NULL, strategy TEXT NOT NULL, reason TEXT NOT
NULL, covered_ranges_json TEXT NOT NULL, before_tokens INTEGER NOT NULL,
after_tokens INTEGER NOT NULL, input_hash TEXT NOT NULL, and created_at_utc TEXT
NOT NULL, plus an index on run_id/created_at_utc. Keep version 1 incompatible.
Add validator requirements for the new table and its run/summary foreign keys.

- [ ] Step 4: Persist compaction metadata atomically

Extend HarnessSqliteWriteSet validation and SqliteHarnessEventStore.CommitAsync
to insert the compaction row only when the referenced summary payload is in the
same commit, with bounded JSON/length checks. Insert the metadata-only
ContextCompacted event and conversation outbox in the same transaction. Roll
back on any constraint failure.

- [ ] Step 5: Project active context on reads

Load the latest compaction records for the run, parse their bounded ranges, and
filter covered source/sequence entries. Insert each summary at its synthetic
projection order, then return scope-separated entries in deterministic order.
Reject malformed persisted ranges with harness_schema_corrupt rather than
silently exposing a partial context. Keep all historical rows queryable by
existing payload/event APIs.

- [ ] Step 6: Run persistence tests and commit

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj --filter "FullyQualifiedName~HarnessSqliteSchemaTests|FullyQualifiedName~HarnessProjectionTransactionTests|FullyQualifiedName~ModelContextSourceTests"

Expected: all migration, atomicity, projection, and restart tests pass.

    git add backend-dotnet/RuAgent.Harness.Persistence backend-dotnet/RuAgent.Harness.Persistence.Tests
    git commit -m "feat: persist context compaction projections"

---

### Task 5: Integrate automatic and manual compaction in the coordinator

**Files:**
- Modify: backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs
- Modify: backend-dotnet/RuAgent.Harness.Core/AgentRunStateMachine.cs
- Modify: backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessCommit.cs
- Modify: backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs
- Modify: backend-dotnet/RuAgent.Harness.Core.Tests/ModelContextLimitTests.cs
- Modify: backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs
- Test: backend-dotnet/RuAgent.Harness.Core.Tests/ContextCompactionCoordinatorTests.cs

**Interfaces:**
- Consumes: ContextBudgetPolicy, ContextCompactionPlanner, IModelContextLimitProvider, and the event store CAS.
- Produces: AgentRunCoordinator.CompactAsync(RunId, ContextCompactionStrategy, ContextCompactionReason, CancellationToken) and automatic preflight behavior before every model invocation.

- [ ] Step 1: Write failing coordinator tests

Add a fake model that records invocations and an in-memory store that exposes
commits. Assert an over-soft-limit run first commits ModelContextSummary and
ContextCompacted, then invokes the model with the reloaded compacted snapshot.
Assert a too-large protected-only snapshot does not invoke the model and returns
context_limit_reached. Assert calling CompactAsync twice with no eligible entries
returns no_op, and a stale expected version returns commit_conflict.

- [ ] Step 2: Run the focused tests and verify the missing integration

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~ContextCompactionCoordinatorTests

Expected: compile failure for CompactAsync and the new automatic behavior.

- [ ] Step 3: Add coordinator preflight and compaction operation

Inject non-null production defaults for the provider, budget policy, and planner.
Before reserving model budget, load the active snapshot, evaluate the policy, and
commit one normal plan when the soft trigger is exceeded. Reassemble after a
successful commit. If the result still exceeds usable budget, emit
context_limit_reached, transition to WaitingUser, and return before model
reservation/invocation. Implement CompactAsync with a per-run semaphore,
normal/aggressive strategy, no-op/terminal/conflict results, and the same commit
builder. Use ITimeProvider and IIdGenerator; do not call wall-clock/GUID globals
in new code.

- [ ] Step 4: Update existing provider-limit tests

Change the old continuation-summary expectation to assert a model-only
ModelContextSummary and a ContextCompacted event. Keep the no-provider unit
fixture as a direct Core behavior test, but add a production-factory test that
provider limits are always wired.

- [ ] Step 5: Run the Core suite sequentially and commit

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj

Expected: all Core tests pass.

    git add backend-dotnet/RuAgent.Harness.Core backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs backend-dotnet/RuAgent.Harness.Core.Tests
    git commit -m "feat: compact agent context before model steps"

---

### Task 6: Expose the compact route and client contract

**Files:**
- Modify: backend-dotnet/RuAgent.Protocol/Harness/AgentApiContracts.cs
- Modify: backend-dotnet/RuBackend/Harness/AgentV1Service.cs
- Modify: backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs
- Modify: backend-dotnet/RuAgent.Client/AgentApiClient.cs
- Modify: backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs
- Test: backend-dotnet/RuBackend.Tests/Harness/AgentV1EndpointTests.cs
- Test: backend-dotnet/RuBackend.Tests/PublicRouteContractTests.cs
- Test: backend-dotnet/RuAgent.Client.Tests/AgentApiClientTests.cs

**Interfaces:**
- Consumes: AgentRunCoordinator.CompactAsync through
  IAgentRuntimeRootRegistry.TryGetCoordinator(RunId, out AgentRunCoordinator?).
- Produces: POST /agent/v1/runs/{runId}/compact, CompactAgentRunRequest,
  CompactAgentRunResponse, and AgentApiClient.CompactAsync.

- [ ] Step 1: Write the route/client contract tests

Assert the route is present, accepts {} and {"strategy":"aggressive"}, rejects
unknown strategy with 400 invalid_compaction_strategy, returns only run/version/
strategy/estimates/id, maps a missing runtime coordinator to 409 runtime_not_ready,
and maps a coordinator CAS conflict to 409 commit_conflict. Assert the client
sends Bearer auth and parses the response without exposing summary text.

- [ ] Step 2: Run route/client tests and verify missing route/API

Run:
    dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~AgentV1EndpointTests|FullyQualifiedName~PublicRouteContractTests"
    dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --filter FullyQualifiedName~AgentApiClientTests

Expected: route/client contract assertions fail because no compact endpoint or
client method exists.

- [ ] Step 3: Implement the protocol/client DTOs

Add bounded CompactAgentRunRequest and response fields run_id, status, strategy,
compaction_id, before_tokens, after_tokens, and run_version. Add
AgentApiClient.CompactAsync with the existing auth/error mapping and strict
run-ID validation.

- [ ] Step 4: Implement service/endpoint wiring

Add TryGetCoordinator to the runtime root registry. Parse the bounded body using
the existing strict body helper, call the coordinator, and map no_op,
compact_not_available, runtime_not_ready, and commit_conflict to stable HTTP
responses. Register the route directly after continue; do not expose summary
payload content.

- [ ] Step 5: Run route/client tests and commit

Run the two commands from Step 2. Expected: all route/client tests pass.

    git add backend-dotnet/RuAgent.Protocol/Harness/AgentApiContracts.cs backend-dotnet/RuBackend/Harness/AgentV1Service.cs backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs backend-dotnet/RuAgent.Client/AgentApiClient.cs backend-dotnet/RuBackend.Tests backend-dotnet/RuAgent.Client.Tests
    git commit -m "feat: expose manual context compaction"

---

### Task 7: Update current documentation and run final verification

**Files:**
- Modify: docs/architecture.md
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/operations/verification.md
- Modify: docs/operations/agent-tools.md
- Modify: README.md
- Modify: backend-dotnet/README.md
- Modify: tests/test_current_documentation.py
- Test: backend-dotnet/RuBackend.Tests/PublicRouteContractTests.cs

**Interfaces:**
- Consumes: shipped v3 schema, route, event, and provider-preflight behavior.
- Produces: current docs that state the actual compaction/migration/verification contract.

- [ ] Step 1: Write the documentation assertions

Extend the documentation test to require the compact route, schema v3,
automatic 80% trigger/62% target, model-only summary, and fail-closed
post-compaction behavior. Assert current docs no longer claim that the provider
limit always hard-stops before a compaction attempt.

- [ ] Step 2: Run the documentation test and verify stale wording

Run:
    .venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q

Expected: failure identifying the old route/limit wording.

- [ ] Step 3: Update only current documents

Document the active projection, v2-to-v3 migration, compact endpoint/request
shape, safe event metadata, and sequential test commands. Leave historical
design records unchanged. Do not stage unrelated dirty documentation files.

- [ ] Step 4: Run the focused and full sequential verification

Run:
    dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj
    dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj
    dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
    dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj
    .venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q

Expected: every command exits 0. Run the Python suite after the .NET suites,
never concurrently.

- [ ] Step 5: Review the diff and commit the docs

Run:
    git diff --check
    git status --short

Confirm only intended current docs are staged; preserve every unrelated dirty file.
Then commit:
    git add docs/architecture.md docs/operations/runtime-behavior.md docs/operations/verification.md docs/operations/agent-tools.md README.md backend-dotnet/README.md tests/test_current_documentation.py
    git commit -m "docs: document agent context lifecycle"
