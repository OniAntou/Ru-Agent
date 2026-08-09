# Ru ASP.NET Core Memory Ownership Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move structured memory storage, retrieval, privacy policy, clear semantics, and self-learning ownership from Python into a tested ASP.NET Core module while preserving memory.db and the WinUI contract.

**Architecture:** Add a deep MemoryService seam in RuBackend. Its SQLite adapter uses the existing memories and legacy_memory_migrations schema, while policy and queue modules keep retrieval and privacy behavior behind small interfaces. The initial public route uses a feature-selected Python or ASP.NET adapter; ownership switches only after shadow-read and single-writer gates pass.

**Tech Stack:** ASP.NET Core net10.0, C#, Microsoft.Data.Sqlite 10.0.0, xUnit, Microsoft.AspNetCore.Mvc.Testing, JSON golden fixtures, and the existing Python pytest fixtures for memory behavior.

## Global Constraints

- Keep D:\LLM\data\conversations\memory.db, the table schema, UUIDs, UTC serialization, and legacy migration marker compatible.
- Preserve MemoryKind values profile, preference, project, decision, and session_summary.
- Preserve duplicate detection, supersession, expiry, pinned records, scope filtering, the 2,000-record scan limit, scoring weights, minimum score, five-record limit, and context character limit.
- Preserve privacy rejection for credentials, passwords, tokens, URLs, and VTS/voice metadata.
- Use exactly one memory writer at a time. Shadow-read is allowed; dual-write is forbidden.
- DELETE /memory increments a generation, invalidates queued work, waits for the active writer, and then clears memory artifacts.
- Do not log memory content, prompts, responses, API keys, bearer tokens, audio, or VTS metadata in migration diagnostics.
- Run the .NET and Python heavy test suites sequentially.
- Review README.md, backend-dotnet/README.md, route-matrix documents, and docs/aspnetcore-cutover-validation.md after the slice.

---

## File Map

Create these focused modules:

- backend-dotnet/RuBackend/Memory/MemoryKind.cs — stable memory category enum.
- backend-dotnet/RuBackend/Memory/MemoryRecord.cs — immutable record and candidate types.
- backend-dotnet/RuBackend/Memory/MemorySqliteOptions.cs — database/index/legacy paths and limits.
- backend-dotnet/RuBackend/Memory/IMemoryRepository.cs — storage interface.
- backend-dotnet/RuBackend/Memory/SqliteMemoryRepository.cs — schema-compatible SQLite implementation.
- backend-dotnet/RuBackend/Memory/IMemoryPolicy.cs — normalization and privacy seam.
- backend-dotnet/RuBackend/Memory/MemoryPolicy.cs — deterministic extraction/rejection implementation.
- backend-dotnet/RuBackend/Memory/MemoryService.cs — retrieval, write, clear, and queue coordination.
- backend-dotnet/RuBackend/Memory/MemoryLearningQueue.cs — bounded background learning and generation invalidation.
- backend-dotnet/RuBackend/Memory/MemoryEndpoints.cs — direct /memory HTTP contract and ownership selection.
- backend-dotnet/RuBackend.Tests/Memory/MemoryParityFixtureTests.cs — golden behavior tests.
- backend-dotnet/RuBackend.Tests/Memory/SqliteMemoryRepositoryTests.cs — schema and storage tests.
- backend-dotnet/RuBackend.Tests/Memory/MemoryPolicyTests.cs — privacy and extraction tests.
- backend-dotnet/RuBackend.Tests/Memory/MemoryServiceTests.cs — queue, clear, and single-writer tests.
- tests/fixtures/memory-parity.json — synthetic records and retrieval cases shared by tests.

Modify:

- backend-dotnet/RuBackend/RuBackend.csproj — add the pinned SQLite package.
- backend-dotnet/RuBackend/Program.cs — register the module and map the ownership-selected route.
- backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs — inject temporary memory paths and a deterministic ownership mode.
- backend-dotnet/compatibility-route-matrix.json — classify GET /memory and DELETE /memory ownership after each gate.
- docs/aspnetcore-route-matrix.md — document the memory ownership transition.
- docs/aspnetcore-cutover-validation.md — record fresh memory evidence.

## Interfaces and invariants

Use these exact public types between tasks:

~~~csharp
public enum MemoryKind
{
    Profile,
    Preference,
    Project,
    Decision,
    SessionSummary,
}

public sealed record MemoryRecord(
    string Id,
    MemoryKind Kind,
    string Content,
    string? Scope,
    string Evidence,
    double Confidence,
    double Importance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AccessedAt,
    DateTimeOffset? ExpiresAt,
    string Status,
    bool Pinned);

public sealed record MemoryCandidate(
    MemoryKind Kind,
    string Content,
    string? Scope,
    string Evidence,
    double Confidence,
    double Importance,
    DateTimeOffset? ExpiresAt = null,
    string? SupersedesId = null);

public interface IMemoryRepository : IAsyncDisposable
{
    Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, int limit, int maxChars, CancellationToken cancellationToken);
    Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken);
    Task<bool> IsLegacyMigrationRecordedAsync(string sourceHash, CancellationToken cancellationToken);
    Task RecordLegacyMigrationAsync(string sourceHash, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
~~~

The repository owns SQL and transactions. MemoryService owns concurrency and queue ordering. MemoryPolicy owns privacy and candidate normalization. No route or worker adapter may issue SQL directly.

### Task 1: Add parity fixtures and stable memory contracts

**Files:**
- Create: tests/fixtures/memory-parity.json
- Create: backend-dotnet/RuBackend/Memory/MemoryKind.cs
- Create: backend-dotnet/RuBackend/Memory/MemoryRecord.cs
- Create: backend-dotnet/RuBackend.Tests/Memory/MemoryParityFixtureTests.cs

- [ ] **Step 1: Write the failing fixture test**

Create a test that loads the fixture and asserts the exact categories, UTC timestamps, candidate fields, and expected retrieval order. Use synthetic content only, including one scoped D:\\LLM record, one pinned record, one expired record, and one duplicate candidate.

~~~csharp
[Fact]
public void Fixture_contains_the_memory_contract_cases()
{
    var fixture = MemoryFixture.Load();

    Assert.Equal(
        new[] { MemoryKind.Project, MemoryKind.Preference, MemoryKind.Decision },
        fixture.ActiveRecords.Select(record => record.Kind));
    Assert.Equal("2026-08-10T00:00:00+00:00", fixture.ActiveRecords[0].CreatedAt.ToString("O"));
    Assert.Equal(new[] { "project-scoped", "pinned-preference" }, fixture.RetrievalCases[0].ExpectedIds);
}
~~~

Run:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryParityFixtureTests
~~~

Expected: FAIL because the memory types and fixture loader do not exist.

- [ ] **Step 2: Add the fixture and immutable types**

Add MemoryKind with the five exact enum values, MemoryRecord, and MemoryCandidate exactly as defined above. Add an internal MemoryFixture loader in the test project that deserializes tests/fixtures/memory-parity.json with JsonStringEnumConverter and DateTimeOffset.

- [ ] **Step 3: Run the focused test**

Run the same command. Expected: PASS with one fixture test passing.

- [ ] **Step 4: Commit**

~~~powershell
git add tests/fixtures/memory-parity.json backend-dotnet/RuBackend/Memory backend-dotnet/RuBackend.Tests/Memory/MemoryParityFixtureTests.cs
git commit -m "test: define ASP.NET memory parity fixtures"
~~~

### Task 2: Add SQLite dependency, options, schema initialization, and read operations

**Files:**
- Modify: backend-dotnet/RuBackend/RuBackend.csproj
- Create: backend-dotnet/RuBackend/Memory/MemorySqliteOptions.cs
- Create: backend-dotnet/RuBackend/Memory/IMemoryRepository.cs
- Create: backend-dotnet/RuBackend/Memory/SqliteMemoryRepository.cs
- Create: backend-dotnet/RuBackend.Tests/Memory/SqliteMemoryRepositoryTests.cs

**Interfaces:**
- Consumes: MemoryRecord, MemoryCandidate, and the fixture from Task 1.
- Produces: IMemoryRepository and SqliteMemoryRepository with the exact methods in the interface block above.

- [ ] **Step 1: Write failing schema/read tests**

Add tests that create a temporary SQLite file, initialize the repository, insert fixture rows with raw test SQL, and verify GetAsync, ListActiveAsync, expired-record filtering, legacy migration marker reads/writes, and DisposeAsync.

~~~csharp
[Fact]
public async Task ListActive_excludes_expired_unpinned_records()
{
    await using var repository = await CreateRepositoryAsync();
    await InsertRawAsync(repository, expiredUnpinnedRecord);
    await InsertRawAsync(repository, activeRecord);

    var records = await repository.ListActiveAsync(CancellationToken.None);

    Assert.DoesNotContain(records, record => record.Id == expiredUnpinnedRecord.Id);
    Assert.Contains(records, record => record.Id == activeRecord.Id);
}
~~~

Run the focused repository tests. Expected: FAIL because the package and implementation are absent.

- [ ] **Step 2: Pin Microsoft.Data.Sqlite and define options**

Add this package reference to RuBackend.csproj:

~~~xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.0" />
~~~

Define MemorySqliteOptions with DatabasePath, LegacyTextsPath, RetrieveScanLimit = 2000, RetrieveMinScore = 0.18, RetrieveLookbackDays = 90, and DefaultContextMaxChars = 1200.

- [ ] **Step 3: Implement schema-compatible initialization and reads**

SqliteMemoryRepository must:

1. create the parent directory;
2. open SQLite with Mode=ReadWriteCreate;Cache=Shared, DefaultTimeout=30, Foreign Keys=True;
3. execute PRAGMA journal_mode=WAL, PRAGMA synchronous=NORMAL, PRAGMA mmap_size=300000000, and PRAGMA temp_store=MEMORY;
4. create the exact memories and legacy_memory_migrations tables and indexes from src/mini_gpt/memory.py;
5. quarantine a corrupt database to memory.db.corrupt before rebuilding;
6. purge expired, unpinned active rows on open;
7. map timestamps as UTC DateTimeOffset and status/kind as validated values;
8. implement IsLegacyMigrationRecordedAsync and RecordLegacyMigrationAsync with INSERT OR IGNORE;
9. serialize all SQL values with parameters.

- [ ] **Step 4: Run focused and existing Python memory tests**

Run sequentially:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteMemoryRepositoryTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_memory.py tests/test_memory_closed.py -q
~~~

Expected: all focused .NET tests pass and the existing Python memory tests stay green.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/RuBackend.csproj backend-dotnet/RuBackend/Memory backend-dotnet/RuBackend.Tests/Memory/SqliteMemoryRepositoryTests.cs
git commit -m "feat: add schema-compatible ASP.NET memory repository"
~~~

### Task 3: Implement retrieval, upsert, supersession, expiry, and clear semantics

**Files:**
- Modify: backend-dotnet/RuBackend/Memory/SqliteMemoryRepository.cs
- Modify: backend-dotnet/RuBackend.Tests/Memory/SqliteMemoryRepositoryTests.cs

- [ ] **Step 1: Add failing behavior tests**

Cover these exact cases:

- same active kind/content/scope returns the existing ID instead of inserting;
- a superseding candidate marks the old row superseded in the same transaction;
- expires_at excludes an unpinned record but not a pinned record;
- a query containing D:\\LLM filters out records from a different scope;
- retrieval computes 0.55 * lexical + 0.15 * scope + 0.05 * importance + 0.03 * confidence + 0.02 * pinned;
- scores below 0.18 are excluded;
- retrieval scans at most 2,000 recent/pinned rows and returns at most five rows within 1,200 rendered characters;
- clear removes memories, legacy_memory_migrations, and registered legacy artifacts.

Run the focused tests. Expected: FAIL for the unimplemented operations.

- [ ] **Step 2: Implement parameterized transactional upsert and clear**

Implement UpsertAsync with the Python duplicate/supersession ordering:

~~~csharp
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
var duplicateId = await FindActiveDuplicateAsync(candidate, transaction, cancellationToken);
if (duplicateId is not null)
{
    await transaction.CommitAsync(cancellationToken);
    return await GetAsync(duplicateId, cancellationToken)
        ?? throw new InvalidOperationException("Duplicate memory disappeared before readback.");
}
if (candidate.SupersedesId is not null)
{
    await MarkSupersededAsync(candidate.SupersedesId, transaction, cancellationToken);
}
await InsertAsync(candidate, transaction, cancellationToken);
await transaction.CommitAsync(cancellationToken);
return await GetAsync(newId, cancellationToken)
    ?? throw new InvalidOperationException("Inserted memory disappeared before readback.");
~~~

Implement retrieval using parameterized SQL for the active/expiry/lookback candidate set, then the exact token overlap and weighted score in C#. Use Regex(@"\w+", RegexOptions.CultureInvariant) only after a parity test proves the Unicode token set matches the Python fixture; otherwise preserve the fixture-approved implementation.

- [ ] **Step 3: Run tests and compare Python results**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteMemoryRepositoryTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_memory.py tests/test_memory_closed.py -q
~~~

Expected: all repository tests pass and existing Python memory behavior is unchanged.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Memory/SqliteMemoryRepository.cs backend-dotnet/RuBackend.Tests/Memory/SqliteMemoryRepositoryTests.cs
git commit -m "feat: preserve memory retrieval and lifecycle semantics"
~~~

### Task 4: Port memory privacy policy and deterministic candidate extraction

**Files:**
- Create: backend-dotnet/RuBackend/Memory/IMemoryPolicy.cs
- Create: backend-dotnet/RuBackend/Memory/MemoryPolicy.cs
- Create: backend-dotnet/RuBackend.Tests/Memory/MemoryPolicyTests.cs
- Modify: tests/fixtures/memory-parity.json

**Interfaces:**
- Consumes: MemoryKind, MemoryCandidate, and the synthetic fixture.
- Produces:

~~~csharp
public interface IMemoryPolicy
{
    IReadOnlyList<MemoryCandidate> ExtractCandidates(string prompt, string response);
    bool IsSensitiveOrUnsafe(string text);
    string StripInternalMetadata(string text);
}
~~~

- [ ] **Step 1: Add failing policy tests**

Add tests for Vietnamese and English preference/project/decision sentences, blank output, credentials, passwords, bearer tokens, URLs, VTS markers, voice pace markers, and duplicate candidate normalization. Assert that private metadata is absent from returned content and that rejected text yields no candidates.

Run:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryPolicyTests
~~~

Expected: FAIL because MemoryPolicy does not exist.

- [ ] **Step 2: Implement the deterministic policy**

Port the patterns and normalization rules from src/mini_gpt/memory_extraction.py without changing their rejection contract. Keep patterns in private Regex fields, use RegexOptions.Compiled | RegexOptions.CultureInvariant, normalize NFC, trim whitespace, cap evidence at 2,000 characters, cap candidate content/evidence at 100,000 characters, and bound confidence/importance to [0,1].

- [ ] **Step 3: Run policy parity tests**

Run the focused .NET tests and the Python extraction tests sequentially:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryPolicyTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_memory_extraction.py tests/test_memory_learning.py -q
~~~

Expected: all tests pass; no secret or private metadata is emitted in test diagnostics.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Memory/IMemoryPolicy.cs backend-dotnet/RuBackend/Memory/MemoryPolicy.cs backend-dotnet/RuBackend.Tests/Memory/MemoryPolicyTests.cs tests/fixtures/memory-parity.json
git commit -m "feat: port memory privacy and extraction policy"
~~~

### Task 5: Add MemoryService and bounded learning queue

**Files:**
- Create: backend-dotnet/RuBackend/Memory/MemoryService.cs
- Create: backend-dotnet/RuBackend/Memory/MemoryLearningQueue.cs
- Create: backend-dotnet/RuBackend.Tests/Memory/MemoryServiceTests.cs

**Interfaces:**
- Consumes: IMemoryRepository, IMemoryPolicy, and the IWorkerClient seam defined in the worker-protocol plan.
- Produces:

~~~csharp
public interface IMemoryService
{
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RememberTurnAsync(string prompt, string response, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    void EnqueueLearning(string prompt, string response);
}
~~~

- [ ] **Step 1: Add failing concurrency tests**

Test that queue capacity is exactly 50, ClearAsync increments generation and discards queued stale turns, a second writer waits for the first, legacy import is idempotent by source hash, worker extraction failure does not kill the queue loop, and DisposeAsync joins the background task without closing the repository while a write is active.

Run the focused tests. Expected: FAIL because the service and queue are absent.

- [ ] **Step 2: Implement the service and queue**

Use Channel<MemoryWorkItem> with BoundedChannelOptions(50) and FullMode = BoundedChannelFullMode.DropOldest. Protect active writes with one SemaphoreSlim(1, 1). Keep a long generation under a lock; capture it when enqueueing and skip work whose generation is stale. ClearAsync acquires the same semaphore, increments generation, drains the channel, calls repository clear, and releases the semaphore.

On service startup, read LegacyTextsPath only when it exists. Hash the source bytes with SHA-256, check IsLegacyMigrationRecordedAsync, parse only valid User/Assistant blocks, run IMemoryPolicy.ExtractCandidates, upsert accepted candidates, and call RecordLegacyMigrationAsync in the same ownership lock. A malformed source returns an imported=0/rejected=1 result and records no destructive change.

Use CancellationToken for shutdown. Catch worker extraction exceptions, record a redacted warning, and continue the loop. Never include prompt, response, or candidate content in the warning.

- [ ] **Step 3: Run focused service and Python lifecycle tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryServiceTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py -q
~~~

Expected: all focused tests pass; the existing Python clear/learning tests remain green.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Memory/MemoryService.cs backend-dotnet/RuBackend/Memory/MemoryLearningQueue.cs backend-dotnet/RuBackend.Tests/Memory/MemoryServiceTests.cs
git commit -m "feat: add ASP.NET memory service and learning queue"
~~~

### Task 6: Add feature-selected memory endpoints and shadow-read comparison

**Files:**
- Create: backend-dotnet/RuBackend/Memory/MemoryEndpoints.cs
- Modify: backend-dotnet/RuBackend/Program.cs
- Modify: backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
- Create: backend-dotnet/RuBackend.Tests/Memory/MemoryEndpointTests.cs
- Modify: backend-dotnet/compatibility-route-matrix.json
- Modify: docs/aspnetcore-route-matrix.md

**Interfaces:**
- Consumes: IMemoryService and BackendOptions ownership configuration.
- Produces: the existing GET /memory array DTO and DELETE /memory success/error JSON.

- [ ] **Step 1: Add failing endpoint tests**

Using WebApplicationFactory<Program>, inject a temporary MemoryService and test:

~~~csharp
[Fact]
public async Task GetMemory_returns_only_the_WinUI_dto_fields()
{
    var response = await Client.GetAsync("/memory");
    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.True(payload[0].TryGetProperty("id", out _));
    Assert.True(payload[0].TryGetProperty("text", out _));
    Assert.True(payload[0].TryGetProperty("category", out _));
    Assert.True(payload[0].TryGetProperty("created_at", out _));
    Assert.False(payload[0].TryGetProperty("evidence", out _));
}
~~~

Add tests for busy clear, unauthorized access, and worker/FastAPI ownership selection. Run the focused tests; expected: FAIL because the direct endpoint does not exist.

- [ ] **Step 2: Implement endpoint ownership selection**

Add RuBackend:Ownership:Memory with values python, shadow, and aspnet. Keep the existing catch-all forwarding route for python. Map direct GET /memory and DELETE /memory only for aspnet; in shadow, call the ASP.NET service after the Python-forwarded response and compare canonical ID, category, timestamp, count, and ordering hashes without logging content.

- [ ] **Step 3: Run route, endpoint, and isolated tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryEndpointTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_aspnetcore_route_matrix.py tests/test_aspnetcore_compatibility_tools.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/check_aspnetcore_route_matrix.py
~~~

Expected: direct endpoint tests pass, the route checker reports the same 38 HTTP routes and two WebSocket routes, and no live data path is modified.

- [ ] **Step 4: Commit the read-ownership slice**

~~~powershell
git add backend-dotnet/RuBackend/Memory/MemoryEndpoints.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests backend-dotnet/compatibility-route-matrix.json docs/aspnetcore-route-matrix.md
git commit -m "feat: add ASP.NET memory ownership seam"
~~~

### Task 7: Execute the memory ownership gate and update documents

**Files:**
- Modify: docs/aspnetcore-cutover-validation.md
- Modify: backend-dotnet/README.md
- Modify: README.md

- [ ] **Step 1: Run all memory-focused checks sequentially**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Memory
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_memory.py tests/test_memory_closed.py tests/test_memory_extraction.py tests/test_memory_learning.py tests/test_server_api.py -q
~~~

Expected: all focused .NET and Python tests pass without data-root leakage.

- [ ] **Step 2: Run the isolated shadow/read harness**

Start a temporary worker/edge topology with a temporary RU_DATA_ROOT, insert the synthetic fixture, verify GET /memory, exercise DELETE /memory, stop both children, and assert that the normal data/conversations/memory.db hash is unchanged. Save only counts, hashes, exit codes, and timing under the ignored artifacts/aspnetcore/ directory.

- [ ] **Step 3: Record evidence and document ownership**

Update the validation document with the exact command, pass counts, ownership mode, data-root isolation result, and rollback result. Update both READMEs to state whether memory is still Python-owned or ASP.NET-owned; do not claim the full FastAPI removal yet.

- [ ] **Step 4: Commit the accepted memory gate**

~~~powershell
git add docs/aspnetcore-cutover-validation.md backend-dotnet/README.md README.md
git commit -m "docs: record ASP.NET memory ownership gate"
~~~

## Self-review checklist

- [ ] Every spec requirement for SQLite compatibility, privacy, queue generation, DTO shape, rollback, and one-writer ownership maps to a task above.
- [ ] Scan this plan for placeholder markers and vague steps; none may remain.
- [ ] Confirm MemoryRecord, MemoryCandidate, IMemoryRepository, IMemoryPolicy, IMemoryService, and options names are identical in every task.
- [ ] Run git diff --check after every plan edit.
