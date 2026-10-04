using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

internal static class HarnessSqliteSchemaValidator
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["agent_workspaces"] =
            [
                "workspace_id",
                "canonical_root_ciphertext",
                "volume_serial_number",
                "root_file_id",
                "display_identity_hash",
                "trust_request_id",
                "trust_decision",
                "trust_source",
                "trust_profile_version",
            ],
            ["agent_session_settings"] =
            [
                "session_id",
                "preset",
                "permission_profile_version",
                "version",
            ],
            ["agent_session_preset_snapshots"] =
            [
                "snapshot_token",
                "session_id",
                "had_previous_selection",
                "previous_preset",
                "previous_permission_profile_version",
                "previous_version",
                "previous_updated_at_utc",
                "applied_preset",
                "applied_permission_profile_version",
                "applied_version",
                "expires_at_utc",
                "consumed_at_utc",
                "created_at_utc",
            ],
            ["agent_runs"] =
            [
                "run_id",
                "session_id",
                "workspace_id",
                "state",
                "version",
                "last_sequence",
                "permission_profile_version",
                "session_preset_version",
                "idempotency_key_sha256",
                "normalized_request_sha256",
                "terminal_at_utc",
                "base_context_captured_at_utc",
                "base_context_entry_count",
            ],
            ["agent_run_provider_selections"] =
            [
                "run_id",
                "backend",
                "alias",
                "protocol",
                "base_uri",
                "model",
                "account",
                "request_options_json",
                "maximum_output_tokens",
                "model_label",
                "calling_mode",
                "context_window_tokens",
                "context_window_source",
                "context_window_version",
                "output_token_reserve",
            ],
            ["agent_payloads"] =
            [
                "payload_id",
                "run_id",
                "kind",
                "audience",
                "protected_content",
                "protection_version",
                "nonce",
                "authentication_tag",
                "consumed_at_utc",
                "context_scope",
                "context_source_id",
                "context_sequence",
                "context_role",
                "context_entry_kind",
            ],
            ["agent_events"] =
            [
                "event_id",
                "run_id",
                "sequence",
                "kind",
                "payload_id",
                "provider_call_id",
                "tool_outcome",
                "side_effects_possible",
            ],
            ["agent_conversation_outbox"] =
            [
                "intent_id",
                "run_id",
                "session_id",
                "payload_id",
                "delivery_state",
                "claim_token",
                "lease_expires_at_utc",
                "last_error_code",
            ],
            ["agent_global_rules"] = ["rule_id", "rule_document", "version"],
            ["agent_project_rules"] = ["rule_id", "workspace_id", "rule_document"],
            ["agent_run_rules"] = ["rule_id", "run_id", "rule_document"],
            ["agent_context_compactions"] =
            [
                "compaction_id",
                "run_id",
                "summary_payload_id",
                "expected_run_version",
                "strategy",
                "reason",
                "covered_ranges_json",
                "before_tokens",
                "after_tokens",
                "input_hash",
                "created_at_utc",
            ],
            ["workspace_coordinators"] = ["workspace_id", "mode", "commit_sequence", "policy_json"],
            ["workspace_run_members"] = ["member_id", "workspace_id", "run_id", "lease_expires_at_utc"],
            ["workspace_path_claims"] = ["claim_id", "workspace_id", "run_id", "normalized_path", "state"],
            ["workspace_change_journal"] = ["change_id", "workspace_id", "commit_sequence", "protected_payload", "nonce", "authentication_tag"],
            // Versioned since 7; it used to be created lazily on first recovery write.
            ["agent_file_recovery"] = ["recovery_id", "run_id", "canonical_path", "content_sha256", "content_length", "created_at_utc", "consumed_at_utc"],
        };

    internal static async Task<bool> ValidateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await ScalarLongAsync(connection, "PRAGMA foreign_keys", cancellationToken)
                .ConfigureAwait(false) != 1
            || await ScalarLongAsync(connection, "PRAGMA user_version", cancellationToken)
                .ConfigureAwait(false) != HarnessSqliteSchema.CurrentVersion
            || !string.Equals(
                await ScalarStringAsync(connection, "PRAGMA journal_mode", cancellationToken)
                    .ConfigureAwait(false),
                "wal",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                await ScalarStringAsync(connection, "PRAGMA quick_check", cancellationToken)
                    .ConfigureAwait(false),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var (table, requiredColumns) in RequiredColumns)
        {
            var actualColumns = await ReadColumnsAsync(connection, table, cancellationToken)
                .ConfigureAwait(false);
            if (requiredColumns.Any(column => !actualColumns.Contains(column)))
            {
                return false;
            }
        }

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name IN (
                  'ux_agent_runs_active_session',
                  'ux_agent_payloads_context_source',
                  'ux_agent_payloads_context_sequence',
                  'ix_agent_conversation_outbox_dispatch',
                  'ix_agent_context_compactions_run_created',
                  'ix_workspace_path_claims_active',
                  'ix_workspace_change_journal_sequence',
                  'ix_agent_session_preset_snapshots_expiry',
                  'ix_agent_session_preset_snapshots_session',
                  'ix_agent_runs_session_created_run')
            """;
        return (long)(await indexCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L) == 10;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
