# Ru ASP.NET Core Deterministic Business Stores Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move settings, credentials, sessions, legacy history, and activity-log ownership into tested ASP.NET Core modules while preserving every existing local file contract and WinUI response.

**Architecture:** Introduce one DataPaths module and one writer per deterministic store. SettingsStore, SessionStore, HistoryStore, and ActivityLogStore expose narrow interfaces and keep JSON/JSONL compatibility behind atomic file adapters. Direct ASP.NET endpoints are enabled per store only after the corresponding Python writer is disabled at a restart boundary.

**Tech Stack:** ASP.NET Core net10.0, C#, System.Text.Json, System.Text.Json.Nodes, Windows Credential Manager P/Invoke, xUnit, WebApplicationFactory, and the existing WinUI API contract tests.

## Global Constraints

- Preserve the current settings keys, aliases, defaults, numeric bounds, boolean fields, and voice-style normalization.
- Preserve current path defaults: memory under data/conversations, settings/history/sessions under the user .minigpt directory, and activity under the existing activity_log.py path; preserve RU_DATA_ROOT test isolation.
- Preserve the session JSON shape, session ID regex, title generation, 200-message cap, and eight context-turn cap.
- Preserve legacy history migration semantics: source hash, backup/durable session write, and no destructive deletion before the new session is durable.
- Preserve activity JSONL schema, chronological ordering, read limit 500, and trim limit 5,000.
- API keys remain in Windows Credential Manager; never write them to settings JSON, worker envelopes, logs, fixtures, or activity records.
- Each store has exactly one writer at a time. No dual-write during shadow comparison.
- Backend/model transitions are transactional: runtime state, credentials, and settings either all commit or all restore.
- Run .NET, WinUI, and Python heavy checks sequentially.
- Review README.md, backend-dotnet/README.md, route matrix, validation evidence, and the migration design after the slice.

---

## File Map

Create:

- backend-dotnet/RuBackend/Business/DataPaths.cs — explicit runtime/test path resolution.
- backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs — normalized settings view.
- backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs — atomic settings read/write and alias normalization.
- backend-dotnet/RuBackend/Business/Settings/ICredentialStore.cs — credential seam.
- backend-dotnet/RuBackend/Business/Settings/WindowsCredentialStore.cs — native Credential Manager adapter.
- backend-dotnet/RuBackend/Business/Sessions/SessionDocument.cs — session JSON model.
- backend-dotnet/RuBackend/Business/Sessions/SessionStore.cs — session file lifecycle and context turns.
- backend-dotnet/RuBackend/Business/Sessions/HistoryStore.cs — legacy history read/migration.
- backend-dotnet/RuBackend/Business/Activity/ActivityEvent.cs — JSONL event model.
- backend-dotnet/RuBackend/Business/Activity/ActivityLogStore.cs — append/read/trim/clear.
- backend-dotnet/RuBackend/Business/BusinessEndpoints.cs — direct endpoint registration and ownership selection.
- backend-dotnet/RuBackend.Tests/Business/DataPathsTests.cs
- backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs
- backend-dotnet/RuBackend.Tests/Business/CredentialStoreTests.cs
- backend-dotnet/RuBackend.Tests/Business/SessionStoreTests.cs
- backend-dotnet/RuBackend.Tests/Business/HistoryStoreTests.cs
- backend-dotnet/RuBackend.Tests/Business/ActivityLogStoreTests.cs
- backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs
- tests/fixtures/business-stores.json — synthetic settings/session/history/activity cases.

Modify:

- backend-dotnet/RuBackend/RuBackend.csproj — add Windows interop support only if required by the chosen P/Invoke declarations.
- backend-dotnet/RuBackend/Program.cs — register modules and map direct routes.
- backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs — inject temporary DataPaths and fake credentials/runtime.
- backend-dotnet/compatibility-route-matrix.json — update deterministic route classifications.
- docs/aspnetcore-route-matrix.md
- docs/aspnetcore-cutover-validation.md
- backend-dotnet/README.md
- README.md

## Interfaces and invariants

Use these exact interfaces between tasks:

~~~csharp
public sealed record DataPaths(
    string MemoryDatabase,
    string LegacyMemoryTexts,
    string SettingsFile,
    string HistoryFile,
    string SessionDirectory,
    string ActivityLogFile);

public sealed record SettingsSnapshot(
    JsonObject Raw,
    string Backend,
    string Model,
    string OpenCodeModel,
    string KiloModel,
    string ApiVariant,
    int MaxTokens,
    double Temperature,
    int TopK,
    bool AllowWebSearch,
    bool EnableSelfLearning,
    bool ShareMemoryWithCloud,
    bool KiloReasoningEnabled,
    string CheckpointPath,
    string VoiceLabel,
    string VoiceStyleId,
    double VoiceSpeed,
    bool AutoPaceEnabled,
    bool AutoReadReplies,
    bool ShowSubtitles);

public interface ISettingsStore
{
    Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken);
}

public interface ICredentialStore
{
    Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken);
    Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken);
    Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken);
}

public sealed record SessionMessage(string Role, string Content);

public sealed record SessionDocument(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SessionMessage> Messages);

public sealed record SessionSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record ConversationTurn(string Prompt, string Response);

public interface ISessionStore
{
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken);
    Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken);
    Task<string?> CreateAsync(CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken);
    Task<bool> AppendAsync(string sessionId, IReadOnlyList<SessionMessage> messages, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(string sessionId, int maxTurns, CancellationToken cancellationToken);
}

public interface IHistoryStore
{
    Task<IReadOnlyList<SessionMessage>> ReadLegacyAsync(CancellationToken cancellationToken);
    Task<string?> MigrateToSessionAsync(CancellationToken cancellationToken);
}

public sealed record ActivityEvent(
    double Ts,
    string Iso,
    string Type,
    string? SessionId,
    JsonObject Data);

public interface IActivityLogStore
{
    Task AppendAsync(ActivityEvent activity, CancellationToken cancellationToken);
    Task<IReadOnlyList<ActivityEvent>> ReadAsync(int limit, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
~~~

### Task 1: Freeze path and file-format fixtures

**Files:**
- Create: tests/fixtures/business-stores.json
- Create: backend-dotnet/RuBackend/Business/DataPaths.cs
- Create: backend-dotnet/RuBackend.Tests/Business/DataPathsTests.cs

- [ ] **Step 1: Write failing path/fixture tests**

Test that default paths match the current Python contracts and explicit RU_DATA_ROOT maps to isolated memory/activity artifacts without changing settings/session defaults. Test the JSON fixture has settings aliases, one session with valid and invalid messages, legacy history, and more than 500 activity events.

Run:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~DataPathsTests
~~~

Expected: FAIL because DataPaths does not exist.

- [ ] **Step 2: Implement DataPaths**

Resolve:

1. memory database to project data/conversations/memory.db unless an explicit memory root is configured;
2. legacy memory texts beside the database;
3. settings/history/sessions below Environment.SpecialFolder.UserProfile plus .minigpt;
4. activity to the same default path represented by activity_log.py, with RU_DATA_ROOT overriding it for isolated harnesses;
5. all directories through Path.GetFullPath without normalizing away an explicitly supplied path.

Expose a constructor accepting six explicit paths for tests. Do not create directories in the value object; stores create only their own parent directory.

- [ ] **Step 3: Run the focused tests**

Run the same command. Expected: PASS with path defaults and isolation assertions passing.

- [ ] **Step 4: Commit**

~~~powershell
git add tests/fixtures/business-stores.json backend-dotnet/RuBackend/Business/DataPaths.cs backend-dotnet/RuBackend.Tests/Business/DataPathsTests.cs
git commit -m "test: freeze deterministic business data paths"
~~~

### Task 2: Implement settings normalization, atomic persistence, and credential seam

**Files:**
- Create: backend-dotnet/RuBackend/Business/Settings/SettingsSnapshot.cs
- Create: backend-dotnet/RuBackend/Business/Settings/SettingsStore.cs
- Create: backend-dotnet/RuBackend/Business/Settings/ICredentialStore.cs
- Create: backend-dotnet/RuBackend/Business/Settings/WindowsCredentialStore.cs
- Create: backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs
- Create: backend-dotnet/RuBackend.Tests/Business/CredentialStoreTests.cs

**Interfaces:**
- Consumes: DataPaths from Task 1 and the IWorkerClient/worker runtime seam from the worker-protocol plan for later backend transitions.
- Produces: ISettingsStore and ICredentialStore with the exact signatures above.

- [ ] **Step 1: Write failing settings tests**

Cover empty/missing settings, legacy aliases, default values, numeric bounds, bool rejection, voice-style fallback to original, preservation of unknown JSON properties, atomic replacement, malformed JSON fallback, and concurrent read-modify-write serialization.

~~~csharp
[Fact]
public async Task Read_applies_the_existing_defaults_and_bounds()
{
    await File.WriteAllTextAsync(paths.SettingsFile, """
    {"backend":"gguf","max_tokens":9999,"temperature":99,"top_k":0,"voice_speed":99,"show_subtitles":"yes"}
    """);

    var snapshot = await store.ReadAsync(CancellationToken.None);

    Assert.Equal(1024, snapshot.MaxTokens);
    Assert.Equal(2.0, snapshot.Temperature);
    Assert.Equal(1, snapshot.TopK);
    Assert.Equal(2.0, snapshot.VoiceSpeed);
    Assert.False(snapshot.ShowSubtitles);
}
~~~

Run the settings tests. Expected: FAIL because SettingsStore is absent.

- [ ] **Step 2: Implement SettingsSnapshot and normalized reads**

Keep all known keys from the current _DEFAULT_SETTINGS contract. Read a JSON object, merge defaults, resolve backend/model aliases, clamp max_tokens to 1..1024, temperature to 0.1..2.0, top_k to 1..200, voice_speed to 0.5..2.0, normalize booleans, and map retired voice styles to original. Preserve unknown properties in Raw for forward compatibility.

- [ ] **Step 3: Implement atomic save**

Use a per-file SemaphoreSlim. Write a UTF-8 temporary file in the same directory, flush it, then replace the target with File.Move(temp, target, overwrite: true). Never write credentials into Raw. Reject empty or overlong model/path values using the current API bounds before replacing the file.

- [ ] **Step 4: Implement the Credential Manager adapter and fake**

WindowsCredentialStore uses CredReadW, CredWriteW, and CredDeleteW with the existing service/account pairs: mini_gpt/api_key, mini_gpt/kilo_gateway_api_key, and mini_gpt/gemini_vision_api_key. The adapter trims input, rejects empty secrets, maps not-found to null, and throws a redacted CredentialStoreException for other native failures. CredentialStoreTests use a fake ICredentialStore and never touch the real credential vault.

- [ ] **Step 5: Run settings/credential tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SettingsStoreTests
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~CredentialStoreTests
~~~

Expected: all focused tests pass, including unknown-property preservation and no-secret assertions.

- [ ] **Step 6: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Business/Settings backend-dotnet/RuBackend.Tests/Business/SettingsStoreTests.cs backend-dotnet/RuBackend.Tests/Business/CredentialStoreTests.cs
git commit -m "feat: add ASP.NET settings and credential stores"
~~~

### Task 3: Implement session JSON, context turns, and legacy history migration

**Files:**
- Create: backend-dotnet/RuBackend/Business/Sessions/SessionDocument.cs
- Create: backend-dotnet/RuBackend/Business/Sessions/SessionStore.cs
- Create: backend-dotnet/RuBackend/Business/Sessions/HistoryStore.cs
- Create: backend-dotnet/RuBackend.Tests/Business/SessionStoreTests.cs
- Create: backend-dotnet/RuBackend.Tests/Business/HistoryStoreTests.cs

- [ ] **Step 1: Write failing session tests**

Cover session ID validation with the existing regex, creation fields, list ordering by updated_at descending, title generation from the first user message capped at 50 characters, valid role filtering, 200-message retention, atomic append, delete errors, and extraction of the last eight complete user/ai turns.

Run the focused tests. Expected: FAIL because the stores are absent.

- [ ] **Step 2: Implement SessionStore**

Use one SemaphoreSlim per SessionStore instance and atomic replacement. Keep the JSON shape:

~~~json
{
  "id": "hex-session-id",
  "title": "Cuoc tro chuyen moi",
  "created_at": "2026-08-10T00:00:00+00:00",
  "updated_at": "2026-08-10T00:00:00+00:00",
  "messages": [{"role":"user","content":"..."},{"role":"ai","content":"..."}]
}
~~~

Reject path traversal and IDs outside ^[a-zA-Z0-9_-]{1,64}$. On append, retain only the last 200 valid messages, set a title from the first nonblank user message when the current title is default/blank, and update updated_at in UTC.

- [ ] **Step 3: Implement context extraction**

GetContextTurnsAsync pairs each user message with the next ai message, ignores incomplete or blank pairs, and returns the last maxTurns complete pairs as ConversationTurn values.

- [ ] **Step 4: Implement HistoryStore migration**

Read only a JSON list of role/content objects from the legacy history file. If sessions already exist, return without migration. Otherwise create a session, append valid messages, write a SHA-256 sidecar record in the migration manifest, verify the session file exists and contains the messages, then move the legacy file to a timestamped backup. A failed verification leaves the original file untouched.

- [ ] **Step 5: Run session/history tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SessionStoreTests
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~HistoryStoreTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py -q
~~~

Expected: all focused tests pass and the Python session API remains green.

- [ ] **Step 6: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Business/Sessions backend-dotnet/RuBackend.Tests/Business/SessionStoreTests.cs backend-dotnet/RuBackend.Tests/Business/HistoryStoreTests.cs
git commit -m "feat: add ASP.NET session and history stores"
~~~

### Task 4: Implement activity JSONL append/read/trim/clear

**Files:**
- Create: backend-dotnet/RuBackend/Business/Activity/ActivityEvent.cs
- Create: backend-dotnet/RuBackend/Business/Activity/ActivityLogStore.cs
- Create: backend-dotnet/RuBackend.Tests/Business/ActivityLogStoreTests.cs

- [ ] **Step 1: Write failing activity tests**

Cover missing files, malformed lines, chronological ordering by ts, limit clamping to 1..500, append fields, trim to the newest 5,000 lines, clear, and concurrent append serialization. Include a test proving activity diagnostics reject token/audio/VTS metadata fields while preserving the user-facing event shape.

Run the focused tests. Expected: FAIL because ActivityLogStore is absent.

- [ ] **Step 2: Implement ActivityLogStore**

Use one SemaphoreSlim. Append one UTF-8 JSON object per line with ts and ISO defaults. Read all valid objects under the semaphore, sort by ts ascending, and return the last clamped limit. When line count exceeds 5,000, atomically rewrite only the newest 5,000 lines. Clear removes the file and treats a missing file as success.

- [ ] **Step 3: Run focused tests**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~ActivityLogStoreTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_activity_log.py -q
~~~

Expected: all tests pass and no sensitive fields appear in diagnostics.

- [ ] **Step 4: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Business/Activity backend-dotnet/RuBackend.Tests/Business/ActivityLogStoreTests.cs
git commit -m "feat: add ASP.NET activity log store"
~~~

### Task 5: Map deterministic endpoints and switch ownership per store

**Files:**
- Create: backend-dotnet/RuBackend/Business/BusinessEndpoints.cs
- Modify: backend-dotnet/RuBackend/Program.cs
- Modify: backend-dotnet/RuBackend.Tests/BackendWebApplicationFactory.cs
- Create: backend-dotnet/RuBackend.Tests/Business/BusinessEndpointTests.cs
- Modify: backend-dotnet/compatibility-route-matrix.json
- Modify: docs/aspnetcore-route-matrix.md

**Interfaces:**
- Consumes: ISettingsStore, ICredentialStore, ISessionStore, IHistoryStore, IActivityLogStore, and the ownership configuration.
- Produces: direct ASP.NET routes for status/settings/activity/history/sessions only when their ownership is aspnet.

- [ ] **Step 1: Write failing direct endpoint tests**

Test authenticated direct ASP.NET responses for:

- GET /settings and POST /settings, including aliases and redacted credential status;
- GET /sessions, POST /sessions/new, POST /sessions/{id}/activate, and DELETE /sessions/{id};
- GET /history;
- GET /activity-log?limit=200 and DELETE /activity-log.

Also test that ownership=python forwards the same route to FastAPI and ownership=shadow compares canonical response hashes without emitting response content.

Run:

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~BusinessEndpointTests
~~~

Expected: FAIL because direct endpoints are absent.

- [ ] **Step 2: Implement direct endpoint module**

Add per-group configuration values python, shadow, and aspnet. Keep existing auth, 401, 413, OPTIONS, and 503 behavior. Use the exact WinUI JSON shapes already returned by Python. Never return credential values; return only has_* booleans. Keep WebSocket routes out of this module.

- [ ] **Step 3: Update route ownership evidence**

Change only the classification fields for routes that are actually direct in this slice. Run the AST route checker and verify it still reports 38 HTTP method/path pairs and two WebSocket paths.

- [ ] **Step 4: Run focused integration checks**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~BusinessEndpointTests
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_aspnetcore_route_matrix.py tests/test_aspnetcore_compatibility_tools.py -q
& D:\\LLM\\.venv\\Scripts\\python.exe scripts/check_aspnetcore_route_matrix.py
~~~

Expected: all focused tests pass and the route inventory remains complete.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuBackend/Business/BusinessEndpoints.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests backend-dotnet/compatibility-route-matrix.json docs/aspnetcore-route-matrix.md
git commit -m "feat: add ASP.NET deterministic business endpoints"
~~~

### Task 6: Run store cutover, restart, and rollback gates

**Files:**
- Modify: docs/aspnetcore-cutover-validation.md
- Modify: backend-dotnet/README.md
- Modify: README.md

- [ ] **Step 1: Run focused .NET and Python tests sequentially**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Business
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore --filter FullyQualifiedName~Backend
& D:\\LLM\\.venv\\Scripts\\python.exe -m pytest tests/test_server_api.py tests/test_activity_log.py -q
~~~

Expected: all focused tests pass without changing normal user data.

- [ ] **Step 2: Run isolated restart and rollback probes**

Start the app with temporary RU_DATA_ROOT and store ownership=aspnet. Create a session, save a non-secret setting, append/read/clear activity, restart, and verify all data survives. Stop the edge, restart with ownership=python, and verify the Python compatibility routes can still read the same files. Assert no two writers are active during either run.

- [ ] **Step 3: Update documents with exact ownership**

Record the current group-by-group ownership, test counts, restart result, rollback result, and data-file hashes. Do not state that FastAPI is removable while voice, VTS, observation, and chat still depend on its runtime.

- [ ] **Step 4: Commit the deterministic-store gate**

~~~powershell
git add docs/aspnetcore-cutover-validation.md backend-dotnet/README.md README.md
git commit -m "docs: record deterministic business store cutover"
~~~

## Self-review checklist

- [ ] Every settings, credential, session, history, activity, path, endpoint, restart, and rollback requirement maps to a task above.
- [ ] Scan this plan for placeholder markers and vague steps; none may remain.
- [ ] Confirm DataPaths, SettingsSnapshot, ISettingsStore, ICredentialStore, SessionDocument, ISessionStore, IHistoryStore, ActivityEvent, and IActivityLogStore signatures are identical in every task.
- [ ] Run git diff --check after every plan edit.
