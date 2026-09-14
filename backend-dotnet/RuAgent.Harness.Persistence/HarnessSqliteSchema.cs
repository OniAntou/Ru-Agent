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
    public const int CurrentVersion = 6;

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
            await using (var walCmd = connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                await walCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var existingVersion = await ReadUserVersionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (existingVersion is not 0 and not 2 and not 3 and not 4 and not 5 and not CurrentVersion)
            {
                return HarnessStoreHealth.Unavailable("harness_schema_incompatible");
            }

            if (existingVersion == 2)
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = SchemaVersionTwoToThree + SchemaVersionThreeToFour + SchemaVersionFourFinalGate + SchemaVersionFiveToSix;
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var verCmd = connection.CreateCommand();
                verCmd.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                await verCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (existingVersion == 3)
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = SchemaVersionThreeToFour + SchemaVersionFourFinalGate + SchemaVersionFiveToSix;
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var verCmd = connection.CreateCommand();
                verCmd.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                await verCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (existingVersion == 4)
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = SchemaVersionFourFinalGate + SchemaVersionFiveToSix;
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var verCmd = connection.CreateCommand();
                verCmd.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                await verCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (existingVersion == 5)
            {
                await using var migration = connection.CreateCommand();
                migration.CommandText = SchemaVersionFiveToSix;
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var verCmd = connection.CreateCommand();
                verCmd.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                await verCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (existingVersion == 0)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = SchemaVersionThree + SchemaVersionThreeToFour + SchemaVersionFourFinalGate;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var verCmd = connection.CreateCommand();
                verCmd.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                await verCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

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

    private const string SchemaVersionThree = """
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
            preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
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
            protection_version INTEGER NOT NULL CHECK(protection_version = 1),
            nonce BLOB NOT NULL CHECK(length(nonce) = 12),
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag) = 16),
            consumed_at_utc TEXT NULL,
            context_scope INTEGER NULL CHECK(context_scope IN (0, 1)),
            context_source_id TEXT NULL CHECK(length(context_source_id) BETWEEN 1 AND 128),
            context_sequence INTEGER NULL CHECK(context_sequence > 0),
            context_role INTEGER NULL CHECK(context_role BETWEEN 0 AND 3),
            context_entry_kind INTEGER NULL CHECK(context_entry_kind BETWEEN 0 AND 3),
            created_at_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_payloads_context_source
            ON agent_payloads(run_id, context_source_id)
            WHERE context_scope IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_payloads_context_sequence
            ON agent_payloads(run_id, context_scope, context_sequence)
            WHERE context_scope IS NOT NULL;

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
            tool_outcome INTEGER NULL CHECK(tool_outcome BETWEEN 0 AND 4),
            side_effects_possible INTEGER NULL CHECK(side_effects_possible IN (0, 1)),
            UNIQUE(run_id, sequence)
        );

        CREATE TABLE IF NOT EXISTS agent_conversation_outbox (
            intent_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            session_id TEXT NOT NULL,
            payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
            role INTEGER NOT NULL CHECK(role IN (0, 1)),
            history_wait_kind INTEGER NOT NULL CHECK(history_wait_kind IN (0, 1)),
            delivery_state INTEGER NOT NULL DEFAULT 0 CHECK(delivery_state BETWEEN 0 AND 3),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
            next_attempt_at_utc TEXT NULL,
            claim_token TEXT NULL,
            lease_expires_at_utc TEXT NULL,
            last_error_code TEXT NULL,
            acknowledged_at_utc TEXT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_agent_conversation_outbox_dispatch
            ON agent_conversation_outbox(
                delivery_state,
                next_attempt_at_utc,
                lease_expires_at_utc,
                created_at_utc,
                intent_id);

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

        CREATE TABLE IF NOT EXISTS agent_context_compactions (
            compaction_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            summary_payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
            expected_run_version INTEGER NOT NULL CHECK(expected_run_version >= 0),
            strategy TEXT NOT NULL CHECK(length(strategy) BETWEEN 1 AND 32),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 32),
            covered_ranges_json TEXT NOT NULL CHECK(length(covered_ranges_json) BETWEEN 2 AND 1048576),
            before_tokens INTEGER NOT NULL CHECK(before_tokens >= 0),
            after_tokens INTEGER NOT NULL CHECK(after_tokens >= 0 AND after_tokens <= before_tokens),
            input_hash TEXT NOT NULL CHECK(length(input_hash) = 64),
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_agent_context_compactions_run_created
            ON agent_context_compactions(run_id, created_at_utc, compaction_id);
        """;

    private const string SchemaVersionTwoToThree = """
        CREATE TABLE IF NOT EXISTS agent_context_compactions (
            compaction_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            summary_payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
            expected_run_version INTEGER NOT NULL CHECK(expected_run_version >= 0),
            strategy TEXT NOT NULL CHECK(length(strategy) BETWEEN 1 AND 32),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 32),
            covered_ranges_json TEXT NOT NULL CHECK(length(covered_ranges_json) BETWEEN 2 AND 1048576),
            before_tokens INTEGER NOT NULL CHECK(before_tokens >= 0),
            after_tokens INTEGER NOT NULL CHECK(after_tokens >= 0 AND after_tokens <= before_tokens),
            input_hash TEXT NOT NULL CHECK(length(input_hash) = 64),
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_agent_context_compactions_run_created
            ON agent_context_compactions(run_id, created_at_utc, compaction_id);
        """;

    private const string SchemaVersionThreeToFour = """
        CREATE TABLE IF NOT EXISTS workspace_coordinators (
            workspace_id TEXT PRIMARY KEY REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
            mode TEXT NOT NULL CHECK(mode IN ('exclusive', 'shared')),
            max_concurrent_runs INTEGER NOT NULL CHECK(max_concurrent_runs BETWEEN 1 AND 32),
            lease_ttl_seconds INTEGER NOT NULL CHECK(lease_ttl_seconds BETWEEN 5 AND 600),
            merge_text_conflicts INTEGER NOT NULL CHECK(merge_text_conflicts IN (0, 1)),
            external_writer_detection INTEGER NOT NULL CHECK(external_writer_detection IN (0, 1)),
            commit_sequence INTEGER NOT NULL CHECK(commit_sequence >= 0),
            policy_json TEXT NOT NULL CHECK(length(policy_json) BETWEEN 2 AND 16384),
            version INTEGER NOT NULL CHECK(version > 0),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS workspace_run_members (
            member_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            writer_kind TEXT NOT NULL CHECK(writer_kind IN ('ru_peer', 'unknown_external')),
            state TEXT NOT NULL CHECK(state IN ('active', 'expired', 'released')),
            heartbeat_at_utc TEXT NOT NULL,
            lease_expires_at_utc TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            UNIQUE(workspace_id, run_id)
        );

        CREATE TABLE IF NOT EXISTS workspace_path_claims (
            claim_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            normalized_path TEXT NOT NULL CHECK(length(normalized_path) BETWEEN 1 AND 4096),
            path_kind TEXT NOT NULL CHECK(path_kind IN ('text', 'binary', 'unknown')),
            state TEXT NOT NULL CHECK(state IN ('active', 'expired', 'released', 'conflict')),
            base_sha256 TEXT NULL CHECK(base_sha256 IS NULL OR length(base_sha256) = 64),
            lease_expires_at_utc TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            UNIQUE(workspace_id, run_id, normalized_path)
        );

        CREATE INDEX IF NOT EXISTS ix_workspace_path_claims_active
            ON workspace_path_claims(workspace_id, normalized_path, state, lease_expires_at_utc);

        CREATE TABLE IF NOT EXISTS workspace_file_snapshots (
            snapshot_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            normalized_path TEXT NOT NULL CHECK(length(normalized_path) BETWEEN 1 AND 4096),
            phase TEXT NOT NULL CHECK(phase IN ('base', 'before', 'after', 'merged')),
            sha256 TEXT NOT NULL CHECK(length(sha256) = 64),
            content BLOB NOT NULL CHECK(length(content) <= 16777216),
            encoding TEXT NOT NULL CHECK(length(encoding) BETWEEN 1 AND 32),
            captured_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS workspace_change_journal (
            change_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
            run_id TEXT NULL REFERENCES agent_runs(run_id) ON DELETE SET NULL,
            commit_sequence INTEGER NOT NULL CHECK(commit_sequence > 0),
            normalized_path TEXT NOT NULL CHECK(length(normalized_path) BETWEEN 1 AND 4096),
            before_sha256 TEXT NULL CHECK(before_sha256 IS NULL OR length(before_sha256) = 64),
            after_sha256 TEXT NULL CHECK(after_sha256 IS NULL OR length(after_sha256) = 64),
            protected_payload BLOB NOT NULL,
            nonce BLOB NOT NULL CHECK(length(nonce) = 12),
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag) = 16),
            protection_version INTEGER NOT NULL CHECK(protection_version = 1),
            status TEXT NOT NULL CHECK(status IN ('prepared', 'committed', 'restored', 'rejected')),
            created_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_workspace_change_journal_sequence
            ON workspace_change_journal(workspace_id, commit_sequence, created_at_utc);

        CREATE TABLE IF NOT EXISTS workspace_mutations (
            mutation_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
            run_id TEXT NULL REFERENCES agent_runs(run_id) ON DELETE SET NULL,
            change_id TEXT NULL REFERENCES workspace_change_journal(change_id) ON DELETE SET NULL,
            idempotency_key TEXT NOT NULL CHECK(length(idempotency_key) BETWEEN 1 AND 256),
            operation TEXT NOT NULL CHECK(length(operation) BETWEEN 1 AND 64),
            state TEXT NOT NULL CHECK(state IN ('prepared', 'applied', 'rejected', 'restored')),
            created_at_utc TEXT NOT NULL,
            UNIQUE(workspace_id, idempotency_key)
        );
        """;

    // Named final-gate migration. The legacy unique index remains in place
    // while coordinator tables are introduced and is removed only once the
    // shared-mode schema is ready. This is idempotent for interrupted upgrades.
    private const string SchemaVersionFourFinalGate = """
        DROP INDEX IF EXISTS ux_agent_runs_active_workspace;
        """;

    // Version 6 widens the preset allowlist to Trusted (2). SQLite cannot ALTER
    // a CHECK, so both tables are rebuilt: same columns in the same order, data
    // copied verbatim, then the originals are dropped and renamed back. Foreign
    // keys are off only inside this batch (autocommit statements) and every
    // existing preset value (0, 1) satisfies the new CHECK, so no row can fail.
    private const string SchemaVersionFiveToSix = """
        PRAGMA foreign_keys=OFF;

        CREATE TABLE agent_session_settings_new (
            session_id TEXT PRIMARY KEY,
            preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
            version INTEGER NOT NULL CHECK(version > 0),
            updated_at_utc TEXT NOT NULL
        );
        INSERT INTO agent_session_settings_new(session_id, preset, version, updated_at_utc)
            SELECT session_id, preset, version, updated_at_utc FROM agent_session_settings;
        DROP TABLE agent_session_settings;
        ALTER TABLE agent_session_settings_new RENAME TO agent_session_settings;

        CREATE TABLE agent_runs_new (
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
        INSERT INTO agent_runs_new(run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count)
            SELECT run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count FROM agent_runs;
        DROP TABLE agent_runs;
        ALTER TABLE agent_runs_new RENAME TO agent_runs;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_active_session
            ON agent_runs(session_id)
            WHERE terminal_at_utc IS NULL;

        PRAGMA foreign_keys=ON;
        """;
}
