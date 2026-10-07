using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessSqliteSchemaTests
{
    private static readonly string[] RequiredTables =
    [
        "agent_runs",
        "agent_run_provider_selections",
        "agent_events",
        "agent_payloads",
        "agent_conversation_outbox",
        "agent_global_rules",
        "agent_project_rules",
        "agent_run_rules",
        "agent_workspaces",
        "agent_session_settings",
        "agent_session_tombstones",
        "agent_session_preset_snapshots",
        "agent_context_compactions",
        "workspace_coordinators",
        "workspace_run_members",
        "workspace_path_claims",
        "workspace_change_journal",
        "agent_file_recovery",
    ];

    private static readonly string[] RequiredIndexes =
    [
        "ux_agent_runs_active_session",
        "ix_agent_runs_session_created_run",
        "ux_agent_payloads_context_source",
        "ux_agent_payloads_context_sequence",
        "ix_agent_conversation_outbox_dispatch",
        "ix_agent_context_compactions_run_created",
        "ix_workspace_path_claims_active",
        "ix_workspace_change_journal_sequence",
        "ix_agent_session_preset_snapshots_expiry",
        "ix_agent_session_preset_snapshots_session",
        "ix_agent_events_provider_continuation",
        "ix_agent_events_session_receipts",
    ];

    [Fact]
    public async Task Initialize_creates_wal_schema_and_is_idempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        var second = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);

        Assert.True(second.IsAvailable);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, second.SchemaVersion);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND (name LIKE 'agent_%' OR name LIKE 'workspace_%')";
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await tableCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.All(RequiredTables, name => Assert.Contains(name, names));
        // workspace_mutations and workspace_file_snapshots were never read or
        // written by production code; a fresh install must not create the tables
        // they were dropped from.
        Assert.DoesNotContain("workspace_mutations", names);
        Assert.DoesNotContain("workspace_file_snapshots", names);
        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND (name LIKE '%agent_%' OR name LIKE '%workspace_%')";
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await indexCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
        }

        Assert.All(RequiredIndexes, name => Assert.Contains(name, indexes));
        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await journalCommand.ExecuteScalarAsync());
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        Assert.Equal(
            HarnessSqliteSchema.CurrentVersion,
            (long)(await versionCommand.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task Schema_persists_typed_run_model_context_metadata()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_run_provider_selections)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        Assert.Contains("model_label", columns);
        Assert.Contains("calling_mode", columns);
        Assert.Contains("context_window_tokens", columns);
        Assert.Contains("context_window_source", columns);
        Assert.Contains("context_window_version", columns);
        Assert.Contains("output_token_reserve", columns);
    }

    [Fact]
    public async Task Migrating_from_twelve_adds_the_latest_session_run_index()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE agent_run_provider_selections DROP COLUMN output_token_reserve;
                ALTER TABLE agent_run_provider_selections DROP COLUMN context_window_version;
                ALTER TABLE agent_run_provider_selections DROP COLUMN context_window_source;
                ALTER TABLE agent_run_provider_selections DROP COLUMN context_window_tokens;
                ALTER TABLE agent_run_provider_selections DROP COLUMN calling_mode;
                ALTER TABLE agent_run_provider_selections DROP COLUMN model_label;
                DROP INDEX IF EXISTS ix_agent_runs_session_created_run;
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 12;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var health = await HarnessSqliteSchema.InitializeAsync(
            fixture.Options,
            CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        await using var plan = verify.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT run_id FROM agent_runs WHERE session_id = 'session-x' ORDER BY created_at_utc DESC, run_id DESC LIMIT 1";
        await using var reader = await plan.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync())
        {
            details.Add(reader.GetString(3));
        }

        Assert.Contains(details, detail => detail.Contains(
            "ix_agent_runs_session_created_run",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrating_from_an_alternate_fourteen_heals_null_provider_metadata()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                PRAGMA foreign_keys = OFF;
                ALTER TABLE agent_run_provider_selections DROP COLUMN calling_mode;
                ALTER TABLE agent_run_provider_selections DROP COLUMN model_label;
                ALTER TABLE agent_run_provider_selections ADD COLUMN model_label TEXT NULL;
                ALTER TABLE agent_run_provider_selections ADD COLUMN calling_mode TEXT NULL;
                DROP INDEX IF EXISTS ix_agent_events_provider_continuation;
                DROP INDEX IF EXISTS ix_agent_events_session_receipts;
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 14;
                INSERT INTO agent_run_provider_selections
                    (run_id, backend, alias, protocol, base_uri, model, account, request_options_json, maximum_output_tokens, model_label, calling_mode)
                VALUES
                    ('run_00000000000000000000000000000000', 'OpenAI', 'openai', 'chat_completions', 'https://api.openai.com/v1', 'gpt-x', 'openai_api_key', '{}', 4096, NULL, NULL);
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        await using var row = verify.CreateCommand();
        row.CommandText = "SELECT model_label, calling_mode FROM agent_run_provider_selections WHERE run_id = 'run_00000000000000000000000000000000'";
        await using var reader = await row.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("gpt-x", reader.GetString(0));
        Assert.Equal("native", reader.GetString(1));
    }

    [Fact]
    public async Task Migrating_from_fourteen_adds_bounded_provider_continuation_lookup_index()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP INDEX IF EXISTS ix_agent_events_provider_continuation; DROP INDEX IF EXISTS ix_agent_events_session_receipts; DROP TABLE IF EXISTS agent_session_work_states; PRAGMA user_version = 14;";
            await downgrade.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(17, health.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        await using var index = verify.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_agent_events_provider_continuation'";
        Assert.Equal(1L, (long)(await index.ExecuteScalarAsync() ?? 0L));
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_agent_events_session_receipts'";
        Assert.Equal(1L, (long)(await index.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task Migrating_from_sixteen_adds_the_session_tombstone_table()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP TABLE IF EXISTS agent_session_tombstones; PRAGMA user_version = 16;";
            await downgrade.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var migrated = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(migrated.IsAvailable, migrated.ErrorCode);
        Assert.Equal(17, migrated.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        Assert.True(await ColumnExistsAsync(verify, "agent_session_tombstones", "session_id"));
        Assert.True(await ColumnExistsAsync(verify, "agent_session_tombstones", "deleted_at_utc"));
    }

    [Fact]
    public async Task Migrating_from_fifteen_adds_encrypted_session_work_state_table()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP INDEX ix_agent_session_work_states_workspace; DROP TABLE agent_session_work_states; PRAGMA user_version = 15;";
            await downgrade.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var migrated = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(migrated.IsAvailable, migrated.ErrorCode);
        Assert.Equal(17, migrated.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        Assert.True(await ColumnExistsAsync(verify, "agent_session_work_states", "ciphertext"));
        await using var index = verify.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_agent_events_session_receipts'";
        Assert.Equal(1L, (long)(await index.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task Migrating_legacy_provider_pin_backfills_fixed_mode_and_unknown_context()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        var created = await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, created.Outcome);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                UPDATE agent_run_provider_selections
                SET backend = 'Custom OpenAI', alias = 'custom', protocol = 'chat_completions',
                    base_uri = 'https://provider.invalid/v1', model = 'legacy-model',
                    account = 'custom_api_key', request_options_json = '{}', maximum_output_tokens = NULL
                WHERE run_id = $run_id;
                ALTER TABLE agent_run_provider_selections DROP COLUMN output_token_reserve;
                ALTER TABLE agent_run_provider_selections DROP COLUMN context_window_version;
                ALTER TABLE agent_run_provider_selections DROP COLUMN context_window_source;
                ALTER TABLE agent_run_provider_selections DROP COLUMN context_window_tokens;
                ALTER TABLE agent_run_provider_selections DROP COLUMN calling_mode;
                ALTER TABLE agent_run_provider_selections DROP COLUMN model_label;
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 13;
                """;
            seed.Parameters.AddWithValue("$run_id", request.Run.RunId.Value);
            await seed.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);
        var selection = await new SqliteAgentRunProviderSelectionStore(fixture.Options)
            .GetAsync(request.Run.RunId, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal("legacy-model", selection?.Selection?.ModelLabel);
        Assert.Equal("native", selection?.Selection?.CallingMode);
        Assert.Null(selection?.Selection?.ContextWindowTokens);
        Assert.Equal("unknown", selection?.Selection?.ContextWindowSource);
        Assert.Equal(AgentRunProviderSelection.DefaultOutputTokenReserve, selection?.Selection?.OutputTokenReserve);
    }

    [Fact]
    public async Task Migrating_from_eleven_leaves_legacy_runs_without_provider_pins()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        var created = await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, created.Outcome);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP TABLE agent_run_provider_selections; DROP INDEX IF EXISTS ix_agent_runs_session_created_run; DROP TABLE IF EXISTS agent_session_work_states; PRAGMA user_version = 11;";
            await downgrade.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);
        var legacySelection = await new SqliteAgentRunProviderSelectionStore(fixture.Options)
            .GetAsync(request.Run.RunId, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        Assert.Null(legacySelection);
    }

    [Fact]
    public async Task Initialize_adds_durable_session_preset_snapshots_without_losing_existing_data()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_session_settings(session_id, preset, permission_profile_version, version, updated_at_utc)
                VALUES ('session-snapshot-sentinel', 'standard', 1, 4, '2026-08-24T01:02:03.0000000+00:00');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT preset, permission_profile_version, version FROM agent_session_settings WHERE session_id = 'session-snapshot-sentinel'";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("standard", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(4L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Migration_backup_preserves_snapshot_schema_and_existing_rows()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_session_settings(session_id, preset, permission_profile_version, version, updated_at_utc)
                VALUES ('session-backup-sentinel', 'plan', 1, 3, '2026-08-24T01:02:03.0000000+00:00');
                """;
            await seed.ExecuteNonQueryAsync();
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var backupPath = Path.Combine(fixture.RootPath, "agent_harness.backup.db");
        File.Copy(fixture.Options.DatabasePath, backupPath);
        var backupOptions = new HarnessSqliteOptions(backupPath);
        var health = await HarnessSqliteSchema.InitializeAsync(backupOptions, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
        {
            await backup.OpenAsync();
            await using var read = backup.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM agent_session_preset_snapshots";
            Assert.Equal(0L, (long)(await read.ExecuteScalarAsync() ?? 0L));
            read.CommandText = "SELECT preset, version FROM agent_session_settings WHERE session_id = 'session-backup-sentinel'";
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("plan", reader.GetString(0));
            Assert.Equal(3L, reader.GetInt64(1));
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Initialize_migrates_version_eight_with_provider_call_relationship()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var initial = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);
        Assert.True(initial.IsAvailable, initial.ErrorCode);
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE agent_run_provider_selections; DROP TABLE agent_session_preset_snapshots; DROP INDEX IF EXISTS ix_agent_runs_session_created_run; ALTER TABLE agent_events DROP COLUMN provider_call_id;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "DROP TABLE IF EXISTS agent_session_work_states; PRAGMA user_version = 8;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(8L, (long)(await command.ExecuteScalarAsync() ?? 0L));
            Assert.False(await ColumnExistsAsync(connection, "agent_events", "provider_call_id"));
        }

        SqliteConnection.ClearAllPools();
        var migrated = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(migrated.IsAvailable, migrated.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, migrated.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        Assert.True(await ColumnExistsAsync(verify, "agent_events", "provider_call_id"));
    }

    [Fact]
    public async Task Initialize_migrates_version_nine_additively_to_snapshot_schema()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE agent_run_provider_selections;
                DROP TABLE agent_session_preset_snapshots;
                DROP INDEX IF EXISTS ix_agent_runs_session_created_run;
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 9;
                INSERT INTO agent_session_settings(session_id, preset, permission_profile_version, version, updated_at_utc)
                VALUES ('session-v9-sentinel', 'standard', 1, 6, '2026-08-24T01:02:03.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);
        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var verify = fixture.CreateRawConnection();
        await verify.OpenAsync();
        await using var table = verify.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'agent_session_preset_snapshots'";
        Assert.Equal(1L, (long)(await table.ExecuteScalarAsync() ?? 0L));
        await using var read = verify.CreateCommand();
        read.CommandText = "SELECT preset, version FROM agent_session_settings WHERE session_id = 'session-v9-sentinel'";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("standard", reader.GetString(0));
        Assert.Equal(6L, reader.GetInt64(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Migrating_from_seven_resets_every_session_preset_to_plan_version_one(int legacyPreset)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await PrepareVersionSevenAsync(fixture);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                VALUES ($session_id, $preset, 7, '2026-08-29T00:00:00.0000000+00:00');
                """;
            seed.Parameters.AddWithValue("$session_id", $"session_{legacyPreset:x32}");
            seed.Parameters.AddWithValue("$preset", legacyPreset);
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        await using var read = migrated.CreateCommand();
        read.CommandText = """
            SELECT preset, permission_profile_version
            FROM agent_session_settings
            WHERE session_id = $session_id
            """;
        read.Parameters.AddWithValue("$session_id", $"session_{legacyPreset:x32}");
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("plan", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Migrating_from_seven_resets_every_run_to_plan_profile_version_zero()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(1);
        await PrepareVersionSevenAsync(fixture);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_runs(
                    run_id,
                    session_id,
                    workspace_id,
                    state,
                    history_wait_kind,
                    version,
                    last_sequence,
                    budget_window,
                    model_steps_in_window,
                    tool_calls_in_window,
                    preset,
                    session_preset_version,
                    policy_snapshot_version,
                    policy_snapshot_sha256,
                    idempotency_key_sha256,
                    normalized_request_sha256,
                    created_at_utc,
                    updated_at_utc,
                    terminal_at_utc,
                    terminal_error_code,
                    base_context_captured_at_utc,
                    base_context_entry_count)
                VALUES (
                    $run_id,
                    $session_id,
                    $workspace_id,
                    7,
                    NULL,
                    0,
                    0,
                    1,
                    0,
                    0,
                    $preset,
                    1,
                    1,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    $idempotency_key,
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    '2026-09-01T00:00:00+00:00',
                    '2026-09-01T00:00:00+00:00',
                    '2026-09-01T00:00:00+00:00',
                    NULL,
                    NULL,
                    0);
                """;
            for (var legacyPreset = 0; legacyPreset <= 2; legacyPreset++)
            {
                seed.Parameters.Clear();
                seed.Parameters.AddWithValue("$run_id", $"run_{legacyPreset:x32}");
                seed.Parameters.AddWithValue("$session_id", $"legacy_{legacyPreset:x32}");
                seed.Parameters.AddWithValue("$workspace_id", identity.WorkspaceId.Value);
                seed.Parameters.AddWithValue("$preset", legacyPreset);
                seed.Parameters.AddWithValue(
                    "$idempotency_key",
                    new string((char)('1' + legacyPreset), 64));
                await seed.ExecuteNonQueryAsync();
            }
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        await using var read = migrated.CreateCommand();
        read.CommandText = """
            SELECT COUNT(*)
            FROM agent_runs
            WHERE preset <> 'plan' OR permission_profile_version <> 0
            """;
        Assert.Equal(0L, (long)(await read.ExecuteScalarAsync() ?? -1L));
        await using var storage = migrated.CreateCommand();
        storage.CommandText = """
            SELECT typeof(preset), preset
            FROM agent_runs
            ORDER BY run_id
            LIMIT 1
            """;
        await using var storageReader = await storage.ExecuteReaderAsync();
        Assert.True(await storageReader.ReadAsync());
        Assert.Equal("text", storageReader.GetString(0));
        Assert.Equal("plan", storageReader.GetString(1));
    }

    [Fact]
    public async Task Interrupted_version_eight_migration_rolls_back_and_can_be_retried()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(1);
        await PrepareVersionSevenAsync(fixture);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                VALUES ('rollback_v8_sentinel', 1, 7, '2026-08-29T00:00:00.0000000+00:00');
                INSERT INTO agent_runs(
                    run_id,
                    session_id,
                    workspace_id,
                    state,
                    history_wait_kind,
                    version,
                    last_sequence,
                    budget_window,
                    model_steps_in_window,
                    tool_calls_in_window,
                    preset,
                    session_preset_version,
                    policy_snapshot_version,
                    policy_snapshot_sha256,
                    idempotency_key_sha256,
                    normalized_request_sha256,
                    created_at_utc,
                    updated_at_utc,
                    terminal_at_utc,
                    terminal_error_code,
                    base_context_captured_at_utc,
                    base_context_entry_count)
                VALUES (
                    'run_77777777777777777777777777777777',
                    'rollback_v8_sentinel',
                    $workspace_id,
                    0,
                    NULL,
                    0,
                    3,
                    1,
                    0,
                    0,
                    2,
                    1,
                    1,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                    '2026-09-01T00:00:00+00:00',
                    '2026-09-01T00:00:00+00:00',
                    NULL,
                    NULL,
                    NULL,
                    0);
                UPDATE agent_workspaces
                SET trust_decision = 1,
                    trust_decided_at_utc = '2026-09-01T00:00:01+00:00'
                WHERE workspace_id = $workspace_id;
                CREATE TABLE agent_runs_v8 (conflict_marker TEXT NOT NULL);
                INSERT INTO agent_runs_v8(conflict_marker) VALUES ('preexisting');
                """;
            seed.Parameters.AddWithValue("$workspace_id", identity.WorkspaceId.Value);
            await seed.ExecuteNonQueryAsync();
        }

        var attempted = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.False(attempted.IsAvailable);
        await using (var verify = fixture.CreateRawConnection())
        {
            await verify.OpenAsync();
            await using var version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(7L, (long)(await version.ExecuteScalarAsync() ?? -1L));
            await using var session = verify.CreateCommand();
            session.CommandText = """
                SELECT typeof(preset), version
                FROM agent_session_settings
                WHERE session_id = 'rollback_v8_sentinel'
                """;
            await using (var sessionReader = await session.ExecuteReaderAsync())
            {
                Assert.True(await sessionReader.ReadAsync());
                Assert.Equal("integer", sessionReader.GetString(0));
                Assert.Equal(7L, sessionReader.GetInt64(1));
            }
            await using var run = verify.CreateCommand();
            run.CommandText = """
                SELECT typeof(preset), last_sequence
                FROM agent_runs
                WHERE run_id = 'run_77777777777777777777777777777777'
                """;
            await using (var runReader = await run.ExecuteReaderAsync())
            {
                Assert.True(await runReader.ReadAsync());
                Assert.Equal("integer", runReader.GetString(0));
                Assert.Equal(3L, runReader.GetInt64(1));
            }
            await using var profileColumns = verify.CreateCommand();
            profileColumns.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM pragma_table_info('agent_session_settings')
                     WHERE name = 'permission_profile_version')
                  + (SELECT COUNT(*) FROM pragma_table_info('agent_runs')
                     WHERE name = 'permission_profile_version')
                """;
            Assert.Equal(0L, (long)(await profileColumns.ExecuteScalarAsync() ?? -1L));
            await using var workspace = verify.CreateCommand();
            workspace.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM pragma_table_info('agent_workspaces')
                     WHERE name IN ('trust_source', 'trust_profile_version')),
                    trust_decision,
                    trust_decided_at_utc
                FROM agent_workspaces
                WHERE workspace_id = $workspace_id
                """;
            workspace.Parameters.AddWithValue("$workspace_id", identity.WorkspaceId.Value);
            await using (var workspaceReader = await workspace.ExecuteReaderAsync())
            {
                Assert.True(await workspaceReader.ReadAsync());
                Assert.Equal(0L, workspaceReader.GetInt64(0));
                Assert.Equal(1L, workspaceReader.GetInt64(1));
                Assert.Equal("2026-09-01T00:00:01+00:00", workspaceReader.GetString(2));
            }
            await using var sessionReplacement = verify.CreateCommand();
            sessionReplacement.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'agent_session_settings_v8'
                """;
            Assert.Equal(0L, (long)(await sessionReplacement.ExecuteScalarAsync() ?? -1L));
            await using var conflict = verify.CreateCommand();
            conflict.CommandText = "SELECT conflict_marker FROM agent_runs_v8";
            Assert.Equal("preexisting", (string?)await conflict.ExecuteScalarAsync());
            await using var dropConflict = verify.CreateCommand();
            dropConflict.CommandText = "DROP TABLE agent_runs_v8";
            await dropConflict.ExecuteNonQueryAsync();
        }

        var retried = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(retried.IsAvailable, retried.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, retried.SchemaVersion);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        await using var migratedRows = migrated.CreateCommand();
        migratedRows.CommandText = """
            SELECT
                (SELECT preset FROM agent_session_settings
                 WHERE session_id = 'rollback_v8_sentinel'),
                (SELECT permission_profile_version FROM agent_session_settings
                 WHERE session_id = 'rollback_v8_sentinel'),
                (SELECT preset FROM agent_runs
                 WHERE run_id = 'run_77777777777777777777777777777777'),
                (SELECT permission_profile_version FROM agent_runs
                 WHERE run_id = 'run_77777777777777777777777777777777'),
                (SELECT trust_decision FROM agent_workspaces
                 WHERE workspace_id = $workspace_id)
            """;
        migratedRows.Parameters.AddWithValue("$workspace_id", identity.WorkspaceId.Value);
        await using var migratedReader = await migratedRows.ExecuteReaderAsync();
        Assert.True(await migratedReader.ReadAsync());
        Assert.Equal("plan", migratedReader.GetString(0));
        Assert.Equal(1L, migratedReader.GetInt64(1));
        Assert.Equal("plan", migratedReader.GetString(2));
        Assert.Equal(0L, migratedReader.GetInt64(3));
        Assert.True(migratedReader.IsDBNull(4));
    }

    [Fact]
    public async Task Migrating_from_six_adds_file_recovery_and_drops_the_dead_mutations_table()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await PrepareVersionSevenAsync(fixture);

        // Reproduce the pre-v7 shape: the lazily created recovery table is absent
        // and the never-used workspace_mutations table is still present.
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DROP TABLE IF EXISTS agent_file_recovery;
                CREATE TABLE IF NOT EXISTS workspace_mutations (
                    mutation_id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    state TEXT NOT NULL
                );
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 6;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        Assert.True(await TableExistsAsync(migrated, "agent_file_recovery"));
        Assert.False(await TableExistsAsync(migrated, "workspace_mutations"));
    }

    [Fact]
    public async Task Migrating_from_ten_drops_the_dead_snapshot_table()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        // Reproduce the pre-v11 shape: the snapshot table existed but no
        // production code ever read or wrote it.
        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE workspace_file_snapshots (
                    snapshot_id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    run_id TEXT NOT NULL,
                    normalized_path TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    content BLOB NOT NULL,
                    encoding TEXT NOT NULL,
                    captured_at_utc TEXT NOT NULL
                );
                DROP TABLE agent_run_provider_selections;
                DROP INDEX IF EXISTS ix_agent_runs_session_created_run;
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 10;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        Assert.Equal(HarnessSqliteSchema.CurrentVersion, health.SchemaVersion);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        Assert.False(await TableExistsAsync(migrated, "workspace_file_snapshots"));
    }

    [Fact]
    public async Task Migrating_from_six_preserves_recovery_rows_created_before_the_migration()
    {
        // A database that already wrote recovery copies through the old lazy
        // creation must keep them: the migration is CREATE IF NOT EXISTS, not a
        // rebuild.
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await PrepareVersionSevenAsync(fixture);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DROP TABLE IF EXISTS agent_file_recovery;
                CREATE TABLE agent_file_recovery (
                    recovery_id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    canonical_path TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    content_length INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    consumed_at_utc TEXT NULL
                );
                INSERT INTO agent_file_recovery
                    (recovery_id, run_id, canonical_path, content_sha256, content_length, created_at_utc, consumed_at_utc)
                VALUES ('rec_1', 'run_1', 'C:\ws\a.txt', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 3, '2026-01-01T00:00:00Z', NULL);
                DROP TABLE IF EXISTS agent_session_work_states;
                PRAGMA user_version = 6;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);

        Assert.True(health.IsAvailable, health.ErrorCode);
        await using var migrated = fixture.CreateRawConnection();
        await migrated.OpenAsync();
        await using var count = migrated.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM agent_file_recovery WHERE recovery_id = 'rec_1'";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync() ?? 0L));
    }

    private static Task PrepareVersionSevenAsync(HarnessStoreTestFixture fixture) =>
        PrepareVersionSevenAsync(fixture.Options.DatabasePath);

    private static async Task PrepareVersionSevenAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadWrite;Pooling=False");
        await connection.OpenAsync();
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "DROP TABLE IF EXISTS agent_run_provider_selections; DROP TABLE IF EXISTS agent_session_preset_snapshots; DROP INDEX IF EXISTS ix_agent_runs_session_created_run; ALTER TABLE agent_events DROP COLUMN provider_call_id";
            await cleanup.ExecuteNonQueryAsync();
        }

        if (await ColumnExistsAsync(connection, "agent_session_settings", "permission_profile_version"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE IF EXISTS agent_session_settings_v7;
                CREATE TABLE agent_session_settings_v7 (
                    session_id TEXT PRIMARY KEY,
                    preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
                    version INTEGER NOT NULL CHECK(version > 0),
                    updated_at_utc TEXT NOT NULL
                );
                INSERT INTO agent_session_settings_v7(session_id, preset, version, updated_at_utc)
                    SELECT session_id,
                        CASE preset WHEN 'plan' THEN 0 WHEN 'standard' THEN 1 WHEN 'full_access' THEN 2 END,
                        version,
                        updated_at_utc
                    FROM agent_session_settings;
                DROP TABLE agent_session_settings;
                ALTER TABLE agent_session_settings_v7 RENAME TO agent_session_settings;
                """;
            await command.ExecuteNonQueryAsync();
        }

        if (await ColumnExistsAsync(connection, "agent_runs", "permission_profile_version"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE IF EXISTS agent_runs_v7;
                CREATE TABLE agent_runs_v7 (
                    run_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE RESTRICT,
                    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 10),
                    history_wait_kind INTEGER NULL CHECK(history_wait_kind IN (0, 1)),
                    version INTEGER NOT NULL CHECK(version >= 0),
                    last_sequence INTEGER NOT NULL CHECK(last_sequence >= 0),
                    budget_window INTEGER NOT NULL CHECK(budget_window > 0),
                    model_steps_in_window INTEGER NOT NULL CHECK(model_steps_in_window >= 0),
                    tool_calls_in_window INTEGER NOT NULL CHECK(tool_calls_in_window >= 0),
                    preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
                    session_preset_version INTEGER NOT NULL CHECK(session_preset_version > 0),
                    policy_snapshot_version INTEGER NOT NULL CHECK(policy_snapshot_version > 0),
                    policy_snapshot_sha256 TEXT NOT NULL CHECK(length(policy_snapshot_sha256) = 64),
                    idempotency_key_sha256 TEXT NOT NULL CHECK(length(idempotency_key_sha256) = 64),
                    normalized_request_sha256 TEXT NOT NULL CHECK(length(normalized_request_sha256) = 64),
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    terminal_at_utc TEXT NULL,
                    terminal_error_code TEXT NULL,
                    base_context_captured_at_utc TEXT NULL,
                    base_context_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(base_context_entry_count >= 0),
                    UNIQUE(session_id, idempotency_key_sha256)
                );
                INSERT INTO agent_runs_v7(
                    run_id,
                    session_id,
                    workspace_id,
                    state,
                    history_wait_kind,
                    version,
                    last_sequence,
                    budget_window,
                    model_steps_in_window,
                    tool_calls_in_window,
                    preset,
                    session_preset_version,
                    policy_snapshot_version,
                    policy_snapshot_sha256,
                    idempotency_key_sha256,
                    normalized_request_sha256,
                    created_at_utc,
                    updated_at_utc,
                    terminal_at_utc,
                    terminal_error_code,
                    base_context_captured_at_utc,
                    base_context_entry_count)
                    SELECT
                        run_id,
                        session_id,
                        workspace_id,
                        state,
                        history_wait_kind,
                        version,
                        last_sequence,
                        budget_window,
                        model_steps_in_window,
                        tool_calls_in_window,
                        CASE preset WHEN 'plan' THEN 0 WHEN 'standard' THEN 1 WHEN 'full_access' THEN 2 END,
                        session_preset_version,
                        policy_snapshot_version,
                        policy_snapshot_sha256,
                        idempotency_key_sha256,
                        normalized_request_sha256,
                        created_at_utc,
                        updated_at_utc,
                        terminal_at_utc,
                        terminal_error_code,
                        base_context_captured_at_utc,
                        base_context_entry_count
                    FROM agent_runs;
                DROP TABLE agent_runs;
                ALTER TABLE agent_runs_v7 RENAME TO agent_runs;
                CREATE UNIQUE INDEX ux_agent_runs_active_session
                    ON agent_runs(session_id)
                    WHERE terminal_at_utc IS NULL;
                """;
            await command.ExecuteNonQueryAsync();
        }

        if (await ColumnExistsAsync(connection, "agent_workspaces", "trust_source"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE agent_workspaces DROP COLUMN trust_source;
                ALTER TABLE agent_workspaces DROP COLUMN trust_profile_version;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var version = connection.CreateCommand();
        version.CommandText = "DROP TABLE IF EXISTS agent_session_work_states; PRAGMA user_version = 7";
        await version.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) > 0;
    }

    [Fact]
    public async Task Initialize_reports_unavailable_without_throwing_for_an_invalid_database_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-harness-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new HarnessSqliteOptions(root);

            var health = await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);

            Assert.False(health.IsAvailable);
            Assert.Equal("harness_store_unavailable", health.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_creates_workspace_coordinator_tables_with_required_columns()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();

        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(workspace_coordinators)";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("workspace_id", columns);
        Assert.Contains("mode", columns);
        Assert.Contains("commit_sequence", columns);
        Assert.Contains("policy_json", columns);
    }

    [Fact]
    public async Task Initialize_rejects_a_legacy_version_one_database_without_reinterpreting_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-harness-invalid-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE agent_runs(run_id TEXT PRIMARY KEY);
                    DROP TABLE IF EXISTS agent_session_work_states;
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var health = await HarnessSqliteSchema.InitializeAsync(
                new HarnessSqliteOptions(databasePath),
                CancellationToken.None);

            Assert.False(health.IsAvailable);
            Assert.Equal("harness_schema_incompatible", health.ErrorCode);
        }
        finally
        {
            // All pools, not one string: pools are keyed by the full connection
            // string, and these tests open the same file through several
            // different option sets.
            SqliteConnection.ClearAllPools();

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_migrates_version_two_additively_and_preserves_existing_rows()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-schema-migration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            var options = new HarnessSqliteOptions(databasePath);
            var initial = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);
            Assert.True(initial.IsAvailable, initial.ErrorCode);
            await PrepareVersionSevenAsync(databasePath);

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                    VALUES ('migration-sentinel', 0, 7, '2026-08-29T00:00:00.0000000+00:00');
                    DROP INDEX IF EXISTS ix_agent_context_compactions_run_created;
                    DROP TABLE IF EXISTS agent_context_compactions;
                    DROP TABLE IF EXISTS agent_session_work_states;
                    PRAGMA user_version = 2;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var migrated = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);

            Assert.True(migrated.IsAvailable, migrated.ErrorCode);
            Assert.Equal(HarnessSqliteSchema.CurrentVersion, migrated.SchemaVersion);
            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync();
            await using var sentinel = verify.CreateCommand();
            sentinel.CommandText =
                "SELECT version FROM agent_session_settings WHERE session_id = 'migration-sentinel'";
            Assert.Equal(7L, (long)(await sentinel.ExecuteScalarAsync() ?? 0L));
            await using var table = verify.CreateCommand();
            table.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'agent_context_compactions'";
            Assert.Equal(1L, (long)(await table.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            // All pools, not one string: pools are keyed by the full connection
            // string, and these tests open the same file through several
            // different option sets.
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// A migration that fails partway must leave the database exactly as it was.
    /// </summary>
    /// <remarks>
    /// The upgrade batch and its version bump used to be autocommit statements, so a failure
    /// or a kill in the middle left the old schema partly replaced while <c>user_version</c>
    /// still said "not upgraded" -- and the version 5 to 6 rebuild is not idempotent, so the
    /// next start could not open the database at all. The batch is one transaction now: a
    /// deterministic failure inside it (here a row that violates the widened table's CHECK)
    /// rolls the whole rebuild back, including the <c>DROP TABLE</c> that is the irreversible
    /// step. A retry then starts from the same, fully intact state instead of a wreckage.
    /// </remarks>
    [Fact]
    public async Task A_version_five_upgrade_that_fails_partway_leaves_the_database_untouched()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-schema-failed-migration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            var options = new HarnessSqliteOptions(databasePath);
            var initial = await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);
            Assert.True(initial.IsAvailable, initial.ErrorCode);
            await PrepareVersionSevenAsync(databasePath);

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO agent_workspaces(workspace_id, canonical_root_nonce, canonical_root_ciphertext, canonical_root_tag, volume_serial_number, root_file_id, display_identity_hash, trust_request_id, trust_requested_at_utc, trust_expires_at_utc, created_at_utc, updated_at_utc)
                    VALUES ('ws_88888888888888888888888888888888', zeroblob(12), zeroblob(1), zeroblob(16), 'VOL', 'fid', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'trust-failed', '2026-09-01T00:00:00+00:00', '2026-09-02T00:00:00+00:00', '2026-09-01T00:00:00+00:00', '2026-09-01T00:00:00+00:00');
                    INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                    VALUES ('rollback-sentinel', 1, 7, '2026-08-29T00:00:00.0000000+00:00');
                    INSERT INTO agent_runs(run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count)
                    VALUES ('run_99999999999999999999999999999999', 'rollback-sentinel', 'ws_88888888888888888888888888888888', 7, NULL, 0, 3, 1, 0, 0, 0, 1, 1, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', '2026-09-01T00:00:00+00:00', '2026-09-01T00:00:00+00:00', NULL, NULL, NULL, 0);
                    -- The migration must abort somewhere inside its batch. A version 5 table
                    -- can hold a state the version 6 CHECK(state BETWEEN 0 AND 10) rejects,
                    -- and the guard is what lets this seed exist at all; the rebuild then
                    -- fails on its first INSERT ... SELECT, after the DROP it must undo.
                    PRAGMA ignore_check_constraints = ON;
                    UPDATE agent_runs SET state = 99 WHERE run_id = 'run_99999999999999999999999999999999';
                    PRAGMA ignore_check_constraints = OFF;
                    DROP TABLE IF EXISTS agent_session_work_states;
                    PRAGMA user_version = 5;
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            var attempted = await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);

            // A row the new schema cannot accept is refused rather than silently dropped.
            Assert.False(attempted.IsAvailable);
            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync();
            await using var version = verify.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(5L, (long)(await version.ExecuteScalarAsync() ?? -1L));
            await using var sentinel = verify.CreateCommand();
            sentinel.CommandText =
                "SELECT version FROM agent_session_settings WHERE session_id = 'rollback-sentinel'";
            // The DROP TABLE was rolled back with the rest: the row is still readable through
            // the original table, which no longer exists if the transaction leaked.
            Assert.Equal(7L, (long)(await sentinel.ExecuteScalarAsync() ?? 0L));
            await using var rows = verify.CreateCommand();
            rows.CommandText = "SELECT COUNT(*) FROM agent_runs";
            Assert.Equal(1L, (long)(await rows.ExecuteScalarAsync() ?? -1L));
            // The decisive difference. Autocommit left the half-built replacement tables
            // behind while user_version still said 5, so the next start re-ran the rebuild
            // and collided with them; the rebuild is not idempotent, so that database could
            // not be opened again. One transaction takes them with it on the way out.
            await using var leftovers = verify.CreateCommand();
            leftovers.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE '%\\_new' ESCAPE '\\'";
            Assert.Equal(0L, (long)(await leftovers.ExecuteScalarAsync() ?? -1L));
        }
        finally
        {
            // All pools, not one string: pools are keyed by the full connection
            // string, and these tests open the same file through several
            // different option sets.
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Initialize_migrates_version_five_to_eight_preserving_rows_and_using_text_presets()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ru-harness-schema-migration-v6",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agent_harness.db");
        try
        {
            var options = new HarnessSqliteOptions(databasePath);
            var initial = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);
            Assert.True(initial.IsAvailable, initial.ErrorCode);
            await PrepareVersionSevenAsync(databasePath);

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO agent_workspaces(workspace_id, canonical_root_nonce, canonical_root_ciphertext, canonical_root_tag, volume_serial_number, root_file_id, display_identity_hash, trust_request_id, trust_requested_at_utc, trust_expires_at_utc, created_at_utc, updated_at_utc)
                    VALUES ('ws_55555555555555555555555555555555', zeroblob(12), zeroblob(1), zeroblob(16), 'VOL', 'fid', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'trust-1', '2026-09-01T00:00:00+00:00', '2026-09-02T00:00:00+00:00', '2026-09-01T00:00:00+00:00', '2026-09-01T00:00:00+00:00');
                    INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
                    VALUES ('migration-sentinel', 0, 7, '2026-08-29T00:00:00.0000000+00:00');
                    INSERT INTO agent_runs(run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count)
                    VALUES ('run_66666666666666666666666666666666', 'migration-sentinel', 'ws_55555555555555555555555555555555', 7, NULL, 0, 3, 1, 0, 0, 0, 1, 1, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', '2026-09-01T00:00:00+00:00', '2026-09-01T00:00:00+00:00', NULL, NULL, NULL, 0);
                    DROP TABLE IF EXISTS agent_session_work_states;
                    PRAGMA user_version = 5;
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            var migrated = await HarnessSqliteSchema.InitializeAsync(
                options,
                CancellationToken.None);

            Assert.True(migrated.IsAvailable, migrated.ErrorCode);
            Assert.Equal(HarnessSqliteSchema.CurrentVersion, migrated.SchemaVersion);
            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync();
            await using var sentinel = verify.CreateCommand();
            sentinel.CommandText = """
                SELECT version, preset, permission_profile_version
                FROM agent_session_settings
                WHERE session_id = 'migration-sentinel'
                """;
            await using var sentinelReader = await sentinel.ExecuteReaderAsync();
            Assert.True(await sentinelReader.ReadAsync());
            Assert.Equal(7L, sentinelReader.GetInt64(0));
            Assert.Equal("plan", sentinelReader.GetString(1));
            Assert.Equal(1L, sentinelReader.GetInt64(2));
            await using var run = verify.CreateCommand();
            run.CommandText = """
                SELECT last_sequence, preset, permission_profile_version
                FROM agent_runs
                WHERE run_id = 'run_66666666666666666666666666666666'
                """;
            await using var runReader = await run.ExecuteReaderAsync();
            Assert.True(await runReader.ReadAsync());
            Assert.Equal(3L, runReader.GetInt64(0));
            Assert.Equal("plan", runReader.GetString(1));
            Assert.Equal(0L, runReader.GetInt64(2));
            await using var fullAccess = verify.CreateCommand();
            fullAccess.CommandText = """
                INSERT INTO agent_session_settings(
                    session_id,
                    preset,
                    permission_profile_version,
                    version,
                    updated_at_utc)
                VALUES ('full-access-sentinel', 'full_access', 1, 1, '2026-09-01T00:00:00+00:00')
                """;
            await fullAccess.ExecuteNonQueryAsync();
            await using var fullAccessRun = verify.CreateCommand();
            fullAccessRun.CommandText = """
                UPDATE agent_runs
                SET preset = 'full_access', permission_profile_version = 1
                WHERE run_id = 'run_66666666666666666666666666666666'
                """;
            await fullAccessRun.ExecuteNonQueryAsync();
        }
        finally
        {
            // All pools, not one string: pools are keyed by the full connection
            // string, and these tests open the same file through several
            // different option sets.
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
