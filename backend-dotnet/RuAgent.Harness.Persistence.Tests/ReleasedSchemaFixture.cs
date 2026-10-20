using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence.Tests;

internal sealed class ReleasedSchemaFixture : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-released-schema-" + Guid.NewGuid().ToString("N"));
    public HarnessSqliteOptions Options { get; }

    private ReleasedSchemaFixture()
    {
        Directory.CreateDirectory(_root);
        Options = new(Path.Combine(_root, "harness.db"));
    }

    public static async Task<ReleasedSchemaFixture> CreateAsync()
    {
        var fixture = new ReleasedSchemaFixture();
        try
        {
            using var resource = typeof(ReleasedSchemaFixture).Assembly.GetManifestResourceStream(
                "RuAgent.Harness.Persistence.Tests.Fixtures.released-v18.sql")
                ?? throw new InvalidOperationException("Released v18 fixture is missing.");
            using var text = new StreamReader(resource);
            await fixture.ExecuteAsync(await text.ReadToEndAsync());
            await fixture.ExecuteAsync("""
                INSERT INTO agent_workspaces(workspace_id,canonical_root_nonce,canonical_root_ciphertext,canonical_root_tag,
                    volume_serial_number,root_file_id,display_identity_hash,trust_request_id,trust_requested_at_utc,
                    trust_expires_at_utc,trust_decision,trust_decided_at_utc,created_at_utc,updated_at_utc,trust_source)
                VALUES ('ws_11111111111111111111111111111111',zeroblob(12),X'CAFE',zeroblob(16),'volume','file-id',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','trust-release','2026-10-01','2026-10-02',
                    1,'2026-10-01','2026-10-01','2026-10-01','explicit');
                INSERT INTO agent_session_settings VALUES ('released-session','standard',2,7,'2026-10-01');
                INSERT INTO agent_runs(run_id,session_id,workspace_id,state,version,last_sequence,budget_window,
                    model_steps_in_window,tool_calls_in_window,preset,permission_profile_version,session_preset_version,
                    policy_snapshot_version,policy_snapshot_sha256,idempotency_key_sha256,normalized_request_sha256,
                    created_at_utc,updated_at_utc)
                VALUES ('run_22222222222222222222222222222222','released-session','ws_11111111111111111111111111111111',
                    3,4,1,1,2,3,'standard',2,7,1,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','2026-10-01','2026-10-01');
                INSERT INTO agent_payloads(payload_id,run_id,kind,audience,encoding,byte_cap,protected_content,
                    protection_version,nonce,authentication_tag,created_at_utc)
                VALUES ('payload_33333333333333333333333333333333','run_22222222222222222222222222222222',0,3,0,128,
                    X'DEADBEEF',1,zeroblob(12),zeroblob(16),'2026-10-01');
                INSERT INTO agent_events(event_id,run_id,sequence,kind,occurred_at_utc,payload_id,safe_code)
                VALUES ('evt_44444444444444444444444444444444','run_22222222222222222222222222222222',1,
                    'run.created','2026-10-01','payload_33333333333333333333333333333333','recorded');
                INSERT INTO business_sessions VALUES ('released-session',X'010203','{}','2026-10-01');
                INSERT INTO workspace_coordinators VALUES ('ws_11111111111111111111111111111111','exclusive',1,30,
                    0,1,4,'{"mode":"exclusive","max_concurrent_runs":1}',7,'2026-10-01','2026-10-01');
                """);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        do { while (await reader.ReadAsync()) { } }
        while (await reader.NextResultAsync());
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public async Task<string[]> SchemaAsync(string? databasePath = null)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath ?? Options.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type || ':' || name || ':' || sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        // Only the fixture's own pool and GUID directory; other tests retain theirs.
        foreach (var path in Directory.EnumerateFiles(_root, "*.db"))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true, Cache = SqliteCacheMode.Private,
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
