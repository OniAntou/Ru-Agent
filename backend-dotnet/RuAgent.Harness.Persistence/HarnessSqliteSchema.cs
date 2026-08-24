using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

public sealed record HarnessStoreHealth(bool IsAvailable, int SchemaVersion, string? ErrorCode)
{
    public static HarnessStoreHealth Available(int schemaVersion) =>
        new(true, schemaVersion, null);

    public static HarnessStoreHealth Unavailable(string errorCode) =>
        new(false, 0, errorCode);
}

public static class HarnessSqliteSchema
{
    public const int CurrentVersion = 1;

    public static async Task<HarnessStoreHealth> InitializeAsync(
        HarnessSqliteOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var directory = Path.GetDirectoryName(options.DatabasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return HarnessStoreHealth.Unavailable("harness_store_unavailable");
            }

            Directory.CreateDirectory(directory);
            await using var connection = await options.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var existingVersion = await ReadUserVersionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (existingVersion is not 0 and not CurrentVersion)
            {
                return HarnessStoreHealth.Unavailable("harness_schema_incompatible");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = SchemaVersionOne;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!await HarnessSqliteSchemaValidator.ValidateAsync(connection, cancellationToken)
                .ConfigureAwait(false))
            {
                return HarnessStoreHealth.Unavailable("harness_schema_invalid");
            }

            return HarnessStoreHealth.Available(CurrentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return HarnessStoreHealth.Unavailable("harness_store_unavailable");
        }
    }

    private static async Task<long> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static bool IsExpectedStorageFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private const string SchemaVersionOne = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS agent_workspaces (
            workspace_id TEXT PRIMARY KEY,
            canonical_root_nonce BLOB NOT NULL CHECK(length(canonical_root_nonce) = 12),
            canonical_root_ciphertext BLOB NOT NULL CHECK(length(canonical_root_ciphertext) BETWEEN 1 AND 1048576),
            canonical_root_tag BLOB NOT NULL CHECK(length(canonical_root_tag) = 16),
            volume_serial_number TEXT NOT NULL CHECK(length(volume_serial_number) BETWEEN 1 AND 64),
            root_file_id TEXT NOT NULL CHECK(length(root_file_id) BETWEEN 1 AND 128),
            display_identity_hash TEXT NOT NULL CHECK(length(display_identity_hash) = 64),
            trust_request_id TEXT NOT NULL UNIQUE,
            trust_requested_at_utc TEXT NOT NULL,
            trust_expires_at_utc TEXT NOT NULL,
            trust_decision INTEGER NULL CHECK(trust_decision IN (0, 1)),
            trust_decided_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS agent_session_settings (
            session_id TEXT PRIMARY KEY,
            preset INTEGER NOT NULL CHECK(preset IN (0, 1)),
            version INTEGER NOT NULL CHECK(version > 0),
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS agent_runs (
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
            preset INTEGER NOT NULL CHECK(preset IN (0, 1)),
            session_preset_version INTEGER NOT NULL CHECK(session_preset_version > 0),
            policy_snapshot_version INTEGER NOT NULL CHECK(policy_snapshot_version > 0),
            policy_snapshot_sha256 TEXT NOT NULL CHECK(length(policy_snapshot_sha256) = 64),
            idempotency_key_sha256 TEXT NOT NULL CHECK(length(idempotency_key_sha256) = 64),
            normalized_request_sha256 TEXT NOT NULL CHECK(length(normalized_request_sha256) = 64),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            terminal_at_utc TEXT NULL,
            terminal_error_code TEXT NULL,
            UNIQUE(session_id, idempotency_key_sha256)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_active_session
            ON agent_runs(session_id)
            WHERE terminal_at_utc IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_active_workspace
            ON agent_runs(workspace_id)
            WHERE terminal_at_utc IS NULL;

        CREATE TABLE IF NOT EXISTS agent_payloads (
            payload_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            kind INTEGER NOT NULL,
            audience INTEGER NOT NULL,
            encoding INTEGER NOT NULL,
            byte_cap INTEGER NOT NULL CHECK(byte_cap BETWEEN 1 AND 16777216),
            protected_content BLOB NOT NULL,
            protection_version INTEGER NOT NULL DEFAULT 0,
            nonce BLOB NULL,
            authentication_tag BLOB NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS agent_events (
            event_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            sequence INTEGER NOT NULL CHECK(sequence > 0),
            kind TEXT NOT NULL CHECK(length(kind) BETWEEN 1 AND 64),
            occurred_at_utc TEXT NOT NULL,
            payload_id TEXT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
            model_step_id TEXT NULL,
            tool_call_id TEXT NULL,
            execution_id TEXT NULL,
            approval_id TEXT NULL,
            safe_count INTEGER NULL CHECK(safe_count >= 0),
            safe_code TEXT NULL CHECK(length(safe_code) BETWEEN 1 AND 64),
            UNIQUE(run_id, sequence)
        );

        CREATE TABLE IF NOT EXISTS agent_conversation_outbox (
            intent_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            session_id TEXT NOT NULL,
            payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
            role INTEGER NOT NULL CHECK(role IN (0, 1)),
            history_wait_kind INTEGER NOT NULL CHECK(history_wait_kind IN (0, 1)),
            delivery_state INTEGER NOT NULL DEFAULT 0 CHECK(delivery_state BETWEEN 0 AND 2),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
            next_attempt_at_utc TEXT NULL,
            acknowledged_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS agent_global_rules (
            rule_id TEXT PRIMARY KEY,
            rule_document BLOB NOT NULL,
            version INTEGER NOT NULL CHECK(version > 0),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS agent_project_rules (
            rule_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
            rule_document BLOB NOT NULL,
            version INTEGER NOT NULL CHECK(version > 0),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS agent_run_rules (
            rule_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            rule_document BLOB NOT NULL,
            created_at_utc TEXT NOT NULL
        );

        PRAGMA user_version = 1;
        """;
}
