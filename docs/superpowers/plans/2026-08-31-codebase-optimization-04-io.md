# Outbox and File Search I/O Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove unnecessary SQLite idle claim work and duplicate file-prefix reads while retaining recovery, ordering, limits and security.

**Architecture:** Persistence chooses one prioritized outbox work item; Host continues owning its application. Search readers consume one bounded stream inside each existing authority boundary. Do not merge Host and Broker execution owners or add a general storage abstraction.

**Tech Stack:** C#, Microsoft.Data.Sqlite, xUnit, real temporary files/databases.

## Global Constraints

Inherit [master constraints](2026-08-31-codebase-optimization.md). Rollout phase 2. No database schema migration, multi-threaded traversal, regex feature, broader paths, or changed resource caps.

---

## O1 — Read-only idle outbox selection

**Files:**

- Modify: `backend-dotnet/RuAgent.Harness.Persistence/HarnessOutboxContracts.cs`, `HarnessOutboxStore.cs`, `HarnessOutboxTransitionStore.cs`.
- Create if needed for cohesive selection code: `backend-dotnet/RuAgent.Harness.Persistence/HarnessOutboxWorkStore.cs`.
- Modify caller: `backend-dotnet/RuBackend/Harness/AgentConversationOutboxDispatcher.cs`.
- Read: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`, `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteOptions.cs`.
- Extend: `backend-dotnet/RuAgent.Harness.Persistence.Tests/HarnessOutboxTests.cs`, `backend-dotnet/RuBackend.Tests/Harness/AgentOutboxDeliveryTests.cs`.
- Neighbor: `HarnessSessionArtifactStoreTests.cs` in persistence tests and `HarnessSessionDeletionCoordinatorTests.cs` in Host Harness tests.

**New interface:**

```csharp
public abstract record OutboxWork
{
    public sealed record Idle : OutboxWork;
    public sealed record Transition(OutboxRunTransition Value) : OutboxWork;
    public sealed record Delivery(OutboxClaim Value) : OutboxWork;
}
// HarnessOutboxStore:
public Task<OutboxWork> ReadNextWorkAsync(
    DateTimeOffset now, CancellationToken cancellationToken);
```

Keep existing ClaimNextAsync/ReadPendingRunTransitionsAsync source-compatible for other consumers; factor connection-taking private helpers rather than duplicating SQL logic.

Selection algorithm:

1. Open one pooled logical connection.
2. Read prioritized candidates: pending run transition first, then due/expired delivery, preserving each category's existing ordering.
3. If none, return Idle without BEGIN IMMEDIATE or a write transaction.
4. If delivery exists, start the write transaction and re-read priorities/eligibility under that transaction; never claim from an unlocked stale read.
5. Apply the unchanged conditional claim update with fresh token/lease, then commit. If a transition now wins, return it without a claim.
6. Host applies transitions and sessions/memory/activity projections exactly as before; no database lock spans these effects.

Do not increase the existing 250 ms polling interval in this task. Wakeup/backoff changes are optional follow-up requiring their own latency evidence.

- [ ] **Step 1 — Baseline and RED.**

Measure 100 empty DispatchOnceAsync calls: logical connections, work-selection SQL and write transactions. Count PRAGMA separately; pooling already exists. Use real SQLite and a scoped diagnostic/instrumented connection boundary; no fake store returning Idle as performance proof.

Add an empty-DB test:

```csharp
[Fact]
public async Task Empty_outbox_returns_idle()
{
    await using var fixture = await HarnessStoreTestFixture.CreateAsync();
    var store = new HarnessOutboxStore(fixture.Options);
    var work = await store.ReadNextWorkAsync(
        HarnessStoreTestFixture.Timestamp, CancellationToken.None);
    Assert.IsType<OutboxWork.Idle>(work);
}
```

This semantic assertion alone does not prove the performance fix. The regression must also observe zero idle write transactions. Before changing selection, hold a separate write transaction and demonstrate the old idle claim attempts to acquire a write lock; after the fix an empty read path must not require that lock, respecting SQLite journal/shared-cache behavior. If locking mode prevents a stable fixture, use measured SQL tracing rather than a timing assertion.

Retain/add: transition priority over due delivery; future retry not claimed; expired lease new token; two contenders never obtain the same current claim; stale acknowledge conflict; restart recovery; cancellation; deletion with in-flight dispatch; no duplicate session append.

- [ ] **Step 2 — GREEN and caller integration.**

Use the result union in DispatchOnceAsync instead of separate transition-read and claim calls. Preserve outcome names and safe errors. The idle goal is one logical open, one work-selection read, zero write transactions (not zero PRAGMA).

Do not change payload encryption, idempotency keys, SQLite timeout/WAL settings, history role, retry schedule, or effect ordering to satisfy a benchmark.

- [ ] **Step 3 — Verification.**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Persistence.Tests/RuAgent.Harness.Persistence.Tests.csproj -c Release --nologo
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentOutbox|FullyQualifiedName~HarnessSessionDeletion"
```

Record before/after idle and busy counts, plus lease/restart/concurrency outcomes; do not infer physical disk savings from logical connection counts.

## F1 — One bounded search stream, separate authority owners

**Files:**

- Modify: `backend-dotnet/RuAgent.Broker/Tools/Files/FileSearchAdapter.cs`.
- Modify: `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs` only in search/read helpers.
- Read: `backend-dotnet/RuAgent.Broker/Tools/Files/FileToolLimits.cs` and each caller's canonical path/protection checks.
- Extend: `backend-dotnet/RuAgent.Broker.Tests/FileDiscoveryAdapterTests.cs`.
- Add Host regression: `backend-dotnet/RuBackend.Tests/Harness/HarnessFileSearchTests.cs`.
- Create cohesive reader next to each implementation if extraction is justified; no new shared package in this slice.

**Reader contract:** one acquired stream per eligible file; inspect the existing binary prefix while retaining it, then decode bounded UTF-8 including that prefix exactly once. A possible internal core signature in each owner's namespace:

```csharp
internal static string? ReadSearchText(
    Stream stream, int maximumBytes, int binaryProbeBytes,
    CancellationToken cancellationToken);
```

Null means binary under that owner's existing rule; size/encoding/error mappings stay caller-compatible. The caller owns stream disposal and policy checks. Do not assume the two implementations have identical caps/probe length/error behavior: tabulate those before sharing any implementation.

- [ ] **Step 1 — RED and count baseline.**

Instrument real stream opens/bytes at the filesystem boundary in controlled fixtures; keep actual Search code and resolver checks. Baseline probe+read should open twice. New regression requires one open and no prefix re-read for a small valid UTF-8 file.

Existing Broker adapter behavioral example:

```csharp
[Fact]
public void Search_retains_literal_line_matches()
{
    var root = Path.Combine(Path.GetTempPath(), "ru-search-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello\nworld hello\n", new UTF8Encoding(false));
        var result = new FileSearchAdapter(root).Execute(
            new JsonObject { ["path"] = ".", ["query"] = "hello" });
        var lines = result["results"]!.AsArray()
            .Select(item => item!["line"]!.GetValue<int>()).ToArray();
        Assert.Equal(new[] { 1, 2 }, lines);
    }
    finally { Directory.Delete(root, recursive: true); }
}
```

Use namespaces already present in FileDiscoveryAdapterTests. Add both owners' cases: BOM/invalid UTF-8, zero byte within/outside probe, empty file, CRLF/lone LF, exact size cap and one byte over, short reads, file grows mid-read, protected path/junction escape, total search byte/time budget, ordering and truncation metadata.

- [ ] **Step 2 — GREEN.**

Replace IsBinaryFile followed by ReadBoundedText with one retained-prefix read. Support non-seekable/short-read streams in the core; never assume one Stream.Read fills the probe. Preserve any deliberate baseline distinction in binary detection; changed classification requires an explicit regression and explanation.

Do not reuse a pre-policy FileStream or relax path canonicalization. A bounded reader does not solve all path replacement races and must not be represented as sandboxing.

- [ ] **Step 3 — Verify both production consumers and measure.**

```powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~File"
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~HarnessFileSearch"
```

Confirm filters discover nonzero tests. Use fixed fixture sets (small text, cap-sized text, binary, ignored files), record opens/bytes/allocations and opt-in median/p95. Update architecture/verification with measured facts, no blanket filesystem speed claim.
