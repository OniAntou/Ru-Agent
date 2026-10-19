using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

internal static class HarnessSqliteSchemaValidator
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["agent_subagent_user_applies"] = ["root_run_id", "agent_id", "result_version", "action_key", "status", "epoch", "approval_id", "code", "revision"],
            ["agent_subagent_apply_resolutions"] = ["root_run_id", "agent_id", "result_version", "resolution_event_id", "model_step_id", "tool_call_id", "execution_id", "decision"],
            ["agent_subagent_workspace_allocations"] = ["root_run_id", "agent_id", "snapshot_id", "stage", "protection_version", "nonce", "ciphertext", "authentication_tag"],
            ["agent_subagent_change_sets"] = ["run_id", "protection_version", "nonce", "ciphertext", "authentication_tag"],
            ["agent_subagent_change_chunks"] = ["run_id", "file_index", "chunk_index", "protection_version", "nonce", "ciphertext", "authentication_tag"],
            ["agent_subagent_result_outcomes"] = ["agent_id", "run_id", "state", "code"],
            ["agent_internal_runs"] = ["run_id", "root_run_id"],
            ["agent_subagent_families"] = ["root_run_id", "canceled"],
            ["agent_subagent_scopes"] = ["run_id", "protection_version", "nonce", "ciphertext", "authentication_tag"],
            ["agent_subagent_waits"] = ["root_run_id", "invocation_id", "arguments_payload_id", "deadline_at_utc", "delivered"],
            ["agent_subagent_apply_operations"] = ["root_run_id", "agent_id", "result_version", "fingerprint", "status", "applied_files", "code"],
            ["agent_subagent_apply_files"] = ["root_run_id", "agent_id", "result_version", "file_index", "protection_version", "nonce", "ciphertext", "authentication_tag", "receipted"],
            ["agent_subagents"] = ["agent_id", "root_run_id", "internal_session_id", "role", "canceled", "invocation_id", "request_hash"],
            ["agent_subagent_attempts"] = ["agent_id", "attempt_number", "run_id", "created_at_utc", "admitted_at_utc", "deadline_at_utc"],
            ["agent_subagent_messages"] = ["agent_id", "message_id", "payload_id", "consumed_run_id", "continuation_run_id", "continuation_error_code"],
            ["agent_subagent_results"] = ["agent_id", "result_version", "run_id", "payload_id", "delivered"],
            ["agent_subagent_budgets"] = ["root_run_id", "window", "model_steps", "tool_calls"],
            ["agent_subagent_reservations"] = ["root_run_id", "window", "invocation_id", "kind", "amount"],
            ["business_migrations"] = ["name"],
            ["business_settings"] = ["id", "document"],
            ["business_sessions"] = ["session_id", "document", "summary", "updated_at_utc"],
            ["business_activity"] = ["sequence", "event_id", "timestamp", "document"],
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
            ["agent_session_work_states"] =
            [
                "session_id",
                "workspace_id",
                "workspace_identity_sha256",
                "source_run_id",
                "source_event_id",
                "version",
                "protection_version",
                "nonce",
                "ciphertext",
                "authentication_tag",
                "updated_at_utc",
            ],
            ["agent_session_tombstones"] =
            [
                "session_id",
                "deleted_at_utc",
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

        // A current version with the old attempt FK would silently erase cleanup
        // evidence on relation loss even though its columns look correct.
        if (await ScalarLongAsync(connection, """
                SELECT count(*) FROM pragma_foreign_key_list('agent_subagent_scopes')
                WHERE "table"='agent_runs' AND "from"='run_id' AND "to"='run_id' AND on_delete='CASCADE'
                """, cancellationToken).ConfigureAwait(false) != 1
            || await ScalarLongAsync(connection, "SELECT count(*) FROM pragma_foreign_key_list('agent_subagent_scopes')",
                cancellationToken).ConfigureAwait(false) != 1)
            return false;

        // Preparation proof must survive loss of optional family/child metadata.
        if (await ScalarLongAsync(connection, """
                SELECT count(*) FROM pragma_foreign_key_list('agent_subagent_workspace_allocations')
                WHERE "table"='agent_runs' AND "from"='root_run_id' AND "to"='run_id' AND on_delete='CASCADE'
                """, cancellationToken).ConfigureAwait(false) != 1
            || await ScalarLongAsync(connection, "SELECT count(*) FROM pragma_foreign_key_list('agent_subagent_workspace_allocations')",
                cancellationToken).ConfigureAwait(false) != 1)
            return false;

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
                  'ix_agent_runs_session_created_run',
                  'ix_agent_events_provider_continuation',
                  'ix_agent_events_session_receipts',
                  'ix_agent_session_work_states_workspace',
                  'business_sessions_updated',
                  'business_activity_timestamp')
            """;
        return (long)(await indexCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L) == 15;
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
