using Microsoft.Data.Sqlite;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.DatabaseMaintenance;
using RuAgent.Harness.Persistence.Maintenance;
using Xunit;

namespace RuBackend.Tests.DatabaseMaintenance;

public sealed class DatabasePruneCommandTests
{
    [Fact]
    public async Task Prune_defaults_to_dry_run_and_lists_only_old_terminal_sessions_without_pending_outbox()
    {
        var paths = CreatePaths();
        try
        {
            var sessions = new SessionStore(paths);
            var eligible = await CreateOldSessionAsync(sessions, paths.AgentHarnessDatabase);
            var activeRunSession = await CreateOldSessionAsync(sessions, paths.AgentHarnessDatabase);
            var pendingOutboxSession = await CreateOldSessionAsync(sessions, paths.AgentHarnessDatabase);
            await InsertActiveRunAsync(paths.AgentHarnessDatabase, activeRunSession);
            await InsertPendingOutboxAsync(paths.AgentHarnessDatabase, pendingOutboxSession);

            var result = await DatabasePruneCommand.RunAsync(
                paths.AgentHarnessDatabase,
                paths,
                olderThanDays: 30,
                apply: false,
                CancellationToken.None);

            Assert.Equal("dry_run", result.Status);
            Assert.Equal(new[] { eligible }, result.SessionIds);
            Assert.NotNull(await sessions.GetAsync(eligible, CancellationToken.None));
            Assert.NotNull(await sessions.GetAsync(activeRunSession, CancellationToken.None));
            Assert.NotNull(await sessions.GetAsync(pendingOutboxSession, CancellationToken.None));
        }
        finally { DeleteProfile(paths); }
    }

    [Fact]
    public async Task Prune_apply_deletes_only_the_rechecked_eligible_session_through_session_coordinator()
    {
        var paths = CreatePaths();
        try
        {
            var sessions = new SessionStore(paths);
            var eligible = await CreateOldSessionAsync(sessions, paths.AgentHarnessDatabase);
            var activeRunSession = await CreateOldSessionAsync(sessions, paths.AgentHarnessDatabase);
            await InsertActiveRunAsync(paths.AgentHarnessDatabase, activeRunSession);

            var result = await DatabasePruneCommand.RunAsync(
                paths.AgentHarnessDatabase,
                paths,
                olderThanDays: 30,
                apply: true,
                CancellationToken.None);

            Assert.Equal("complete", result.Status);
            Assert.Equal(new[] { eligible }, result.SessionIds);
            Assert.Null(await sessions.GetAsync(eligible, CancellationToken.None));
            Assert.NotNull(await sessions.GetAsync(activeRunSession, CancellationToken.None));
        }
        finally { DeleteProfile(paths); }
    }

    [Fact]
    public async Task Restore_command_rejects_a_profile_owned_by_an_active_host()
    {
        var paths = CreatePaths();
        try
        {
            using var owner = DatabaseOwnerLease.Acquire(paths.AgentHarnessDatabase);
            var exitCode = await DatabaseCommandMode.RunAsync(
                ["--database", "restore", Path.Combine(paths.BusinessDataRoot, "backup"), "--data-root", paths.BusinessDataRoot],
                CancellationToken.None);

            Assert.Equal(3, exitCode);
        }
        finally { DeleteProfile(paths); }
    }

    private static DataPaths CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-database-prune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return DataPaths.Resolve(root, root, root, root);
    }

    private static async Task<string> CreateOldSessionAsync(SessionStore sessions, string databasePath)
    {
        var id = await sessions.CreateAsync(CancellationToken.None);
        Assert.NotNull(id);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE business_sessions SET updated_at_utc = $updated WHERE session_id = $id;";
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.AddDays(-60).ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertActiveRunAsync(string databasePath, string sessionId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await EnableForeignKeysAsync(connection);
        await EnsureWorkspaceAsync(connection);
        await InsertRunAsync(connection, sessionId, terminal: false);
    }

    private static async Task InsertPendingOutboxAsync(string databasePath, string sessionId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await EnableForeignKeysAsync(connection);
        await EnsureWorkspaceAsync(connection);
        var runId = await InsertRunAsync(connection, sessionId, terminal: true);
        var payloadId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_payloads(
                payload_id, run_id, kind, audience, encoding, byte_cap, protected_content,
                protection_version, nonce, authentication_tag, created_at_utc)
            VALUES ($payload, $run, 0, 0, 0, 1, X'01', 1, zeroblob(12), zeroblob(16), $now);
            INSERT INTO agent_conversation_outbox(
                intent_id, run_id, session_id, payload_id, role, history_wait_kind, delivery_state, created_at_utc)
            VALUES ($intent, $run, $session, $payload, 0, 0, 0, $now);
            """;
        command.Parameters.AddWithValue("$intent", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$payload", payloadId);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnableForeignKeysAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureWorkspaceAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO agent_workspaces(
                workspace_id, canonical_root_nonce, canonical_root_ciphertext, canonical_root_tag,
                volume_serial_number, root_file_id, display_identity_hash, trust_request_id,
                trust_requested_at_utc, trust_expires_at_utc, created_at_utc, updated_at_utc)
            VALUES ('workspace', zeroblob(12), X'01', zeroblob(16), 'test-volume', 'test-root',
                $hash, 'database-prune-test-trust', $now, $now, $now, $now);
            """;
        command.Parameters.AddWithValue("$hash", new string('a', 64));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> InsertRunAsync(SqliteConnection connection, string sessionId, bool terminal)
    {
        var runId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_runs(
                run_id, session_id, workspace_id, state, version, last_sequence,
                budget_window, model_steps_in_window, tool_calls_in_window, preset,
                permission_profile_version, session_preset_version, policy_snapshot_version,
                policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256,
                created_at_utc, updated_at_utc, terminal_at_utc)
            VALUES ($run, $session, 'workspace', $state, 0, 0, 1, 0, 0, 'standard',
                1, 1, 1, $hash, $hash, $hash, $now, $now, $terminal);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$hash", new string('a', 64));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$state", terminal ? 7 : 0);
        command.Parameters.AddWithValue("$terminal", terminal ? now : DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return runId;
    }

    private static void DeleteProfile(DataPaths paths)
    {
        SqliteConnection.ClearAllPools();
        var root = paths.BusinessDataRoot;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
