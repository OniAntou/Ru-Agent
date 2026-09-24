using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Policy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class AgentSessionPermissionStoreTests
{
    [Theory]
    [InlineData(AgentPermissionPreset.Plan, "plan")]
    [InlineData(AgentPermissionPreset.Standard, "standard")]
    [InlineData(AgentPermissionPreset.FullAccess, "full_access")]
    public async Task Every_preset_round_trips_as_canonical_text_with_profile_version_one(
        AgentPermissionPreset preset,
        string expectedWireValue)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(preset switch
        {
            AgentPermissionPreset.Plan => 31,
            AgentPermissionPreset.Standard => 32,
            _ => 33,
        });

        var selected = await store.SetInteractivelyAsync(
            session,
            preset,
            expectedVersion: 0,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var loaded = await store.GetAsync(session, CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.UpdatedInteractively, selected.Outcome);
        Assert.Equal(preset, loaded!.Preset);
        Assert.Equal(1L, loaded.Version);
        Assert.Equal("TEXT", await ColumnTypeAsync(fixture, "agent_session_settings", "preset"));
        Assert.Equal("INTEGER", await ColumnTypeAsync(
            fixture,
            "agent_session_settings",
            "permission_profile_version"));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT typeof(preset), preset, permission_profile_version
            FROM agent_session_settings
            WHERE session_id = $session_id
            """;
        command.Parameters.AddWithValue("$session_id", session.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("text", reader.GetString(0));
        Assert.Equal(expectedWireValue, reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Optimistic_version_and_permission_profile_version_advance_independently()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(34);
        Assert.True(await ColumnExistsAsync(
            fixture,
            "agent_session_settings",
            "permission_profile_version"));

        var first = await store.SetInteractivelyAsync(
            session,
            AgentPermissionPreset.Plan,
            expectedVersion: 0,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var second = await store.SetInteractivelyAsync(
            session,
            AgentPermissionPreset.Standard,
            expectedVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1L, first.Selection!.Version);
        Assert.Equal(2L, second.Selection!.Version);
        Assert.Equal(1L, await ScalarAsync(
            fixture,
            "SELECT permission_profile_version FROM agent_session_settings"));
    }

    [Fact]
    public async Task New_run_uses_profile_version_one_and_canonical_preset_text()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        _ = await fixture.RegisterWorkspaceAsync(1);
        Assert.True(await ColumnExistsAsync(
            fixture,
            "agent_runs",
            "permission_profile_version"));
        var request = fixture.NewRun(1, 1, 1, preset: AgentPermissionPreset.Standard);

        var created = await fixture.CreateEventStore().TryCreateRunAsync(
            request,
            CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Created, created.Outcome);
        Assert.Equal(1, created.Run!.PermissionProfileVersion);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT typeof(preset), preset, permission_profile_version
            FROM agent_runs
            WHERE run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", request.Run.RunId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("text", reader.GetString(0));
        Assert.Equal("standard", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task New_run_cannot_start_with_legacy_permission_profile_version_zero()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        _ = await fixture.RegisterWorkspaceAsync(2);
        var request = fixture.NewRun(
            2,
            2,
            2,
            permissionProfileVersion: 0);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None));

        Assert.Equal("request", exception.ParamName);
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_runs"));
    }

    [Theory]
    [InlineData(AgentPermissionPreset.Plan, "plan")]
    [InlineData(AgentPermissionPreset.Standard, "standard")]
    [InlineData(AgentPermissionPreset.FullAccess, "full_access")]
    public void Preset_mapper_round_trips_canonical_values(AgentPermissionPreset preset, string expectedWireValue)
    {
        var wireValue = AgentPermissionPresetMapper.ToWireValue(preset);

        var parsed = AgentPermissionPresetMapper.TryParseWireValue(wireValue, out var roundTripped);

        Assert.Equal(expectedWireValue, wireValue);
        Assert.True(parsed);
        Assert.Equal(preset, roundTripped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("future_preset")]
    public void Preset_mapper_rejects_unknown_wire_values(string? wireValue)
    {
        var parsed = AgentPermissionPresetMapper.TryParseWireValue(wireValue, out _);

        Assert.False(parsed);
    }

    [Fact]
    public async Task A_concurrent_reader_does_not_block_the_preset_write()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(1);

        await using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        await reader.OpenAsync();
        await using var readCommand = reader.CreateCommand();
        readCommand.CommandText = "SELECT preset, version FROM agent_session_settings";
        await using var read = await readCommand.ExecuteReaderAsync();

        var result = await store.SetInteractivelyAsync(
            session,
            AgentPermissionPreset.Standard,
            expectedVersion: 0,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.UpdatedInteractively, result.Outcome);
        Assert.NotNull(result.Selection);
    }

    [Fact]
    public async Task First_standard_run_creates_version_one_then_requires_an_exact_match()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(1);

        var created = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.Standard,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var matched = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.Standard,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var mismatch = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.FullAccess,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.CreatedDefault, created.Outcome);
        Assert.Equal(1, created.Selection!.Version);
        Assert.Equal(SessionPermissionOutcome.Matched, matched.Outcome);
        Assert.Equal(SessionPermissionOutcome.PresetMismatch, mismatch.Outcome);
        Assert.Equal("preset_mismatch", mismatch.ErrorCode);
    }

    [Fact]
    public async Task Full_access_cannot_be_created_implicitly()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);

        var result = await store.EnsureForRunAsync(
            HarnessStoreTestFixture.Session(1),
            AgentPermissionPreset.FullAccess,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.SelectionRequired, result.Outcome);
        Assert.Equal("preset_selection_required", result.ErrorCode);
        Assert.Null(result.Selection);
    }

    [Fact]
    public async Task Sessions_default_independently_and_deletion_removes_only_that_selection()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var firstSession = HarnessStoreTestFixture.Session(1);
        var secondSession = HarnessStoreTestFixture.Session(2);
        await store.EnsureForRunAsync(
            firstSession,
            AgentPermissionPreset.Standard,
            1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        await store.EnsureForRunAsync(
            secondSession,
            AgentPermissionPreset.Standard,
            1,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        await store.DeleteSessionAsync(firstSession, CancellationToken.None);

        Assert.Null(await store.GetAsync(firstSession, CancellationToken.None));
        var second = await store.GetAsync(secondSession, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(AgentPermissionPreset.Standard, second.Preset);
        Assert.Equal(1, second.Version);
    }

    [Fact]
    public async Task Interactive_selection_can_explicitly_enable_full_access()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(1);

        var selected = await store.SetInteractivelyAsync(
            session,
            AgentPermissionPreset.FullAccess,
            expectedVersion: 0,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var run = await store.EnsureForRunAsync(
            session,
            AgentPermissionPreset.FullAccess,
            selectionVersion: 1,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(SessionPermissionOutcome.UpdatedInteractively, selected.Outcome);
        Assert.Equal(AgentPermissionPreset.FullAccess, selected.Selection!.Preset);
        Assert.Equal(SessionPermissionOutcome.Matched, run.Outcome);
    }

    [Fact]
    public async Task Durable_snapshot_restores_prior_selection_and_replays_only_while_state_matches()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(41);
        var firstToken = Token(1);
        var secondToken = Token(2);

        var first = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            firstToken,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var second = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.FullAccess,
            1,
            secondToken,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var restored = await store.RestoreSnapshotAsync(
            session,
            secondToken,
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            CancellationToken.None);
        var replayed = await store.RestoreSnapshotAsync(
            session,
            secondToken,
            HarnessStoreTestFixture.Timestamp.AddMinutes(3),
            CancellationToken.None);
        var later = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Plan,
            1,
            Token(3),
            HarnessStoreTestFixture.Timestamp.AddMinutes(4),
            CancellationToken.None);
        var blockedReplay = await store.RestoreSnapshotAsync(
            session,
            secondToken,
            HarnessStoreTestFixture.Timestamp.AddMinutes(5),
            CancellationToken.None);

        Assert.Equal(SessionPresetMutationOutcome.Updated, first.Outcome);
        Assert.Equal(SessionPresetMutationOutcome.Updated, second.Outcome);
        Assert.Equal(SessionPresetRestoreOutcome.Restored, restored.Outcome);
        Assert.Equal(AgentPermissionPreset.Standard, restored.Selection!.Preset);
        Assert.Equal(1L, restored.Selection.Version);
        Assert.Equal(SessionPresetRestoreOutcome.Replayed, replayed.Outcome);
        Assert.Equal(AgentPermissionPreset.Standard, replayed.Selection!.Preset);
        Assert.Equal(SessionPresetMutationOutcome.Updated, later.Outcome);
        Assert.Equal(SessionPresetRestoreOutcome.ReplayConflict, blockedReplay.Outcome);
        var loaded = await store.GetAsync(session, CancellationToken.None);
        Assert.Equal(AgentPermissionPreset.Plan, loaded!.Preset);
        Assert.Equal(2L, loaded.Version);
    }

    [Fact]
    public async Task Durable_snapshot_restores_new_session_to_no_selection()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(42);
        var token = Token(4);

        var mutation = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            token,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);
        var restored = await store.RestoreSnapshotAsync(
            session,
            token,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(SessionPresetMutationOutcome.Updated, mutation.Outcome);
        Assert.Equal(SessionPresetRestoreOutcome.Restored, restored.Outcome);
        Assert.False(restored.HadPreviousSelection);
        Assert.Null(restored.Selection);
        Assert.Null(await store.GetAsync(session, CancellationToken.None));
    }

    [Fact]
    public async Task Durable_snapshot_capacity_rejects_before_preset_mutation_without_eviction()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(43);
        await InsertSnapshotRowsAsync(fixture, 0, 1024, HarnessStoreTestFixture.Timestamp.AddMinutes(10));

        var result = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.FullAccess,
            0,
            Token(2000),
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        Assert.Equal(SessionPresetMutationOutcome.CapacityExceeded, result.Outcome);
        Assert.Equal("preset_snapshot_capacity_exceeded", result.ErrorCode);
        Assert.Null(result.Selection);
        Assert.Null(await store.GetAsync(session, CancellationToken.None));
        Assert.Equal(1024L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_preset_snapshots"));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE snapshot_token = $token", "$token", Token(0)));
    }

    [Fact]
    public async Task Durable_snapshot_cleans_expired_rows_without_evicting_active_rows()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(44);
        await InsertSnapshotRowsAsync(fixture, 0, 1023, HarnessStoreTestFixture.Timestamp.AddMinutes(10));
        await InsertSnapshotRowsAsync(fixture, 1023, 1, HarnessStoreTestFixture.Timestamp.AddMinutes(-1));

        var result = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            Token(3000),
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        Assert.Equal(SessionPresetMutationOutcome.Updated, result.Outcome);
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE snapshot_token = $token", "$token", Token(1023)));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE snapshot_token = $token", "$token", Token(0)));
    }

    [Fact]
    public async Task Durable_snapshot_rejects_invalid_token_and_session_mismatch_without_mutation()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(45);
        var otherSession = HarnessStoreTestFixture.Session(46);
        var token = Token(4000);
        var mutation = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            token,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        var mismatch = await store.RestoreSnapshotAsync(
            otherSession,
            token,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var loaded = await store.GetAsync(session, CancellationToken.None);

        Assert.Equal(SessionPresetMutationOutcome.Updated, mutation.Outcome);
        Assert.Equal(SessionPresetRestoreOutcome.SessionMismatch, mismatch.Outcome);
        Assert.Equal("preset_snapshot_mismatch", mismatch.ErrorCode);
        Assert.Equal(AgentPermissionPreset.Standard, loaded!.Preset);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Plan,
            1,
            "invalid-token",
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            CancellationToken.None));
        Assert.Equal(1L, (await store.GetAsync(session, CancellationToken.None))!.Version);
    }

    [Fact]
    public async Task Duplicate_snapshot_token_fails_before_a_second_preset_mutation()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(48);
        var token = Token(6000);
        await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            token,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        var duplicate = await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.FullAccess,
            1,
            token,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var loaded = await store.GetAsync(session, CancellationToken.None);

        Assert.Equal(SessionPresetMutationOutcome.TokenConflict, duplicate.Outcome);
        Assert.Equal("preset_snapshot_token_conflict", duplicate.ErrorCode);
        Assert.Equal(AgentPermissionPreset.Standard, loaded!.Preset);
        Assert.Equal(1L, loaded.Version);
    }

    [Fact]
    public async Task Restore_does_not_consume_snapshot_when_applied_version_changed()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(49);
        var firstToken = Token(7000);
        var secondToken = Token(7001);
        await store.SetInteractivelyWithSnapshotAsync(session, AgentPermissionPreset.Standard, 0, firstToken, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await store.SetInteractivelyWithSnapshotAsync(session, AgentPermissionPreset.Plan, 1, secondToken, HarnessStoreTestFixture.Timestamp.AddMinutes(1), CancellationToken.None);

        var conflict = await store.RestoreSnapshotAsync(session, firstToken, HarnessStoreTestFixture.Timestamp.AddMinutes(2), CancellationToken.None);
        var consumed = await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE snapshot_token = $token AND consumed_at_utc IS NOT NULL", "$token", firstToken);

        Assert.Equal(SessionPresetRestoreOutcome.VersionConflict, conflict.Outcome);
        Assert.Equal(0L, consumed);
    }

    [Fact]
    public async Task Restore_rolls_back_selection_when_consume_cas_misses()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var store = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(50);
        var token = Token(8000);
        await store.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            token,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        await using (var connection = fixture.CreateRawConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER force_consume_cas_miss
                BEFORE UPDATE OF consumed_at_utc ON agent_session_preset_snapshots
                WHEN OLD.snapshot_token = '{token}'
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await store.RestoreSnapshotAsync(
            session,
            token,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var loaded = await store.GetAsync(session, CancellationToken.None);
        var consumed = await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE snapshot_token = $token AND consumed_at_utc IS NOT NULL",
            "$token",
            token);

        Assert.Equal(SessionPresetRestoreOutcome.VersionConflict, result.Outcome);
        Assert.NotNull(loaded);
        Assert.Equal(AgentPermissionPreset.Standard, loaded!.Preset);
        Assert.Equal(1L, loaded.Version);
        Assert.Equal(0L, consumed);
    }

    [Fact]
    public async Task Concurrent_restore_serializes_to_one_mutation_and_one_safe_replay()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var firstStore = new AgentSessionPermissionStore(fixture.Options);
        var secondStore = new AgentSessionPermissionStore(fixture.Options);
        var session = HarnessStoreTestFixture.Session(47);
        var token = Token(5000);
        await firstStore.SetInteractivelyWithSnapshotAsync(
            session,
            AgentPermissionPreset.Standard,
            0,
            token,
            HarnessStoreTestFixture.Timestamp,
            CancellationToken.None);

        var firstRestore = firstStore.RestoreSnapshotAsync(
            session,
            token,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var secondRestore = secondStore.RestoreSnapshotAsync(
            session,
            token,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var results = await Task.WhenAll(firstRestore, secondRestore);

        Assert.Single(results, result => result.Outcome == SessionPresetRestoreOutcome.Restored);
        Assert.Single(results, result => result.Outcome == SessionPresetRestoreOutcome.Replayed);
        Assert.Null(await firstStore.GetAsync(session, CancellationToken.None));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE consumed_at_utc IS NOT NULL"));
    }

    private static string Token(int value) => "sps_" + value.ToString("x32");

    private static async Task InsertSnapshotRowsAsync(
        HarnessStoreTestFixture fixture,
        int start,
        int count,
        DateTimeOffset expiresAt)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO agent_session_preset_snapshots(
                snapshot_token,
                session_id,
                had_previous_selection,
                previous_preset,
                previous_permission_profile_version,
                previous_version,
                previous_updated_at_utc,
                applied_preset,
                applied_permission_profile_version,
                applied_version,
                expires_at_utc,
                consumed_at_utc,
                created_at_utc)
            VALUES (
                $token,
                $session_id,
                0,
                NULL,
                NULL,
                NULL,
                NULL,
                'standard',
                1,
                1,
                $expires_at,
                NULL,
                $created_at)
            """;
        var tokenParameter = command.Parameters.Add("$token", SqliteType.Text);
        var sessionParameter = command.Parameters.Add("$session_id", SqliteType.Text);
        var expiresParameter = command.Parameters.Add("$expires_at", SqliteType.Text);
        var createdParameter = command.Parameters.Add("$created_at", SqliteType.Text);
        for (var i = start; i < start + count; i++)
        {
            tokenParameter.Value = Token(i);
            sessionParameter.Value = $"capacity-{i:x32}";
            expiresParameter.Value = expiresAt.ToUniversalTime().ToString("O");
            createdParameter.Value = HarnessStoreTestFixture.Timestamp.ToUniversalTime().ToString("O");
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<bool> ColumnExistsAsync(
        HarnessStoreTestFixture fixture,
        string table,
        string column)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return (long)(await command.ExecuteScalarAsync() ?? 0L) == 1L;
    }

    private static async Task<string?> ColumnTypeAsync(
        HarnessStoreTestFixture fixture,
        string table,
        string column)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM pragma_table_info($table) WHERE name = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<long> ScalarAsync(
        HarnessStoreTestFixture fixture,
        string commandText)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> ScalarAsync(
        HarnessStoreTestFixture fixture,
        string commandText,
        string parameterName,
        object value)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue(parameterName, value);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
