using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Harness.Persistence.Privacy;
using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Persistence.Tests;

internal sealed class HarnessStoreTestFixture : IAsyncDisposable
{
    private readonly FakeHarnessKeyStore _keyStore = new();

    private HarnessStoreTestFixture(string rootPath, KnownSecretRedactor redactor)
    {
        RootPath = rootPath;
        Options = new HarnessSqliteOptions(Path.Combine(rootPath, "agent_harness.db"));
        PayloadStore = new HarnessPayloadStore(
            Options,
            new HarnessPayloadCryptography(_keyStore),
            redactor);
    }

    public string RootPath { get; }

    public HarnessSqliteOptions Options { get; }

    public HarnessPayloadStore PayloadStore { get; }

    public SqliteHarnessEventStore CreateEventStore() => new(Options, PayloadStore, CreateWorkStateStore());

    public SessionWorkStateStore CreateWorkStateStore() => new(
        Options,
        new SessionWorkStateCryptography(_keyStore));

    public static async Task<HarnessStoreTestFixture> CreateAsync(IEnumerable<string>? knownSecrets = null)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-persistence-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var fixture = new HarnessStoreTestFixture(rootPath,
            knownSecrets is null ? KnownSecretRedactor.Empty : new KnownSecretRedactor(knownSecrets));
        var health = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);
        Assert.True(health.IsAvailable, health.ErrorCode);
        return fixture;
    }

    public SqliteConnection CreateRawConnection() =>
        new($"Data Source={Options.DatabasePath};Mode=ReadWrite;Pooling=False");

    public async Task ResetSessionHistorySchemaToV29Async()
    {
        await ResetContextProjectionSchemaToV30Async();
        await using var connection = CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=OFF;
            DROP TRIGGER IF EXISTS business_session_messages_search_insert;
            DROP TRIGGER IF EXISTS business_session_messages_search_delete;
            DROP TRIGGER IF EXISTS business_session_messages_search_update;
            DROP TABLE IF EXISTS business_session_message_search;
            DROP TABLE IF EXISTS business_session_source_receipts;
            DROP TABLE IF EXISTS business_session_messages;
            ALTER TABLE business_sessions DROP COLUMN history_epoch;
            ALTER TABLE business_sessions DROP COLUMN total_message_count;
            ALTER TABLE business_sessions DROP COLUMN client_source;
            ALTER TABLE business_sessions DROP COLUMN last_agent_activity_at_utc;
            ALTER TABLE business_sessions DROP COLUMN forked_from_message_id;
            ALTER TABLE business_sessions DROP COLUMN parent_session_id;
            ALTER TABLE business_sessions DROP COLUMN created_at_utc;
            ALTER TABLE business_sessions DROP COLUMN title;
            PRAGMA foreign_keys=ON;
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task ResetContextProjectionSchemaToV30Async()
    {
        await using var connection = CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS context_payload_updated;
            DROP TRIGGER IF EXISTS context_payload_deleted;
            DROP TRIGGER IF EXISTS context_event_updated;
            DROP TRIGGER IF EXISTS context_event_deleted;
            DROP TRIGGER IF EXISTS context_compaction_updated;
            DROP TRIGGER IF EXISTS context_compaction_deleted;
            DROP INDEX IF EXISTS ix_agent_context_compactions_run_version;
            DROP INDEX IF EXISTS ix_agent_events_execution_request;
            DROP INDEX IF EXISTS ix_business_session_messages_session_source;
            ALTER TABLE agent_runs DROP COLUMN context_revision;
            PRAGMA user_version=30;
            """;
        await command.ExecuteNonQueryAsync();
        // This fixture deliberately moves the schema backwards. A pooled handle
        // from the newer shape is not a faithful process opening a v30 database.
        await using var pooled = await Options.OpenConnectionAsync(default);
        SqliteConnection.ClearPool(pooled);
    }

    public async Task<WorkspaceIdentity> RegisterWorkspaceAsync(int number)
    {
        var identity = Workspace(number);
        var key = await _keyStore.GetOrCreateKeyAsync(CancellationToken.None);
        WorkspaceRootCiphertext encryptedRoot;
        try
        {
            encryptedRoot = WorkspaceRootCryptography.Encrypt(identity.WorkspaceId, key,
                Encoding.UTF8.GetBytes($"C:\\ru-test-workspace-{number}"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        var registration = new WorkspaceRegistration(
            identity,
            encryptedRoot);
        var request = new WorkspaceTrustRequest(
            Trust(number),
            identity,
            Timestamp,
            Timestamp.AddMinutes(10));
        var store = new SqliteWorkspaceStore(Options);
        await store.RegisterAsync(registration, request, CancellationToken.None);
        return identity;
    }

    public NewAgentRun NewRun(
        int runNumber,
        int sessionNumber,
        int workspaceNumber,
        char idempotencyHashCharacter = 'a',
        char requestHashCharacter = 'b',
        AgentPermissionPreset preset = AgentPermissionPreset.Standard,
        long selectionVersion = 1,
        IReadOnlyList<HarnessPayloadIntent>? payloads = null,
        IReadOnlyList<ConversationOutboxIntent>? outbox = null,
        HarnessRunStatus? status = null,
        int? permissionProfileVersion = null)
    {
        var run = new AgentRun(
            Run(runNumber),
            Session(sessionNumber),
            Workspace(workspaceNumber).WorkspaceId,
            status ?? new HarnessRunStatus(HarnessRunState.Created),
            version: 0,
            lastSequence: 1,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            preset,
            policySnapshotVersion: 1,
            policySnapshotSha256: Sha('c'),
            Timestamp,
            Timestamp,
            permissionProfileVersion: permissionProfileVersion);
        var promptPayloadId = payloads?
            .FirstOrDefault(payload => payload.Kind == HarnessPayloadKind.UserPrompt)?
            .PayloadId;
        return new NewAgentRun(
            run,
            [new HarnessEventIntent(Event(runNumber), HarnessEventKind.RunStateChanged, payloadId: promptPayloadId)],
            payloads ?? [],
            outbox ?? [],
            new RunCreationIdentity(
                Sha(idempotencyHashCharacter),
                Sha(requestHashCharacter),
                selectionVersion));
    }

    public static readonly DateTimeOffset Timestamp =
        new(2026, 8, 24, 1, 2, 3, TimeSpan.Zero);

    public static WorkspaceIdentity Workspace(int number) =>
        new(
            new WorkspaceId(Generated("ws_", number)),
            $"volume-{number}",
            $"root-file-{number}",
            Sha((char)('a' + (number % 6))));

    public static SessionId Session(int number) => new(number.ToString("x32"));

    public static RunId Run(int number) => new(Generated("run_", number));

    public static EventId Event(int number) => new(Generated("evt_", number));

    public static PayloadId Payload(int number) => new(Generated("payload_", number));

    public static TrustRequestId Trust(int number) => new(Generated("trust_", number));

    public static string Sha(char character) => new(character, 64);

    public async ValueTask DisposeAsync()
    {
        // Clear every pool, not one connection string: pools are keyed by the
        // full string, so clearing a single string misses every pool opened
        // with different options and leaves its file lock behind.
        SqliteConnection.ClearAllPools();

        await Task.Yield();
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private static string Generated(string prefix, int number) =>
        prefix + number.ToString("x32");
}
