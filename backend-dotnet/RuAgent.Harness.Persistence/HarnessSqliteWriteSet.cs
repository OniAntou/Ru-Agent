using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

internal static class HarnessSqliteWriteSet
{
    internal static void ValidateNewRun(NewAgentRun request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Run.Version != 0)
        {
            throw new ArgumentException("A new run must start at version zero.", nameof(request));
        }

        if (request.Events.Count == 0 || request.Run.LastSequence != request.Events.Count)
        {
            throw new ArgumentException(
                "A new run sequence must exactly cover its initial events.",
                nameof(request));
        }

        ValidateIntentSet(
            request.Events,
            request.Payloads,
            request.ConversationOutbox,
            request.Run.SessionId,
            nameof(request));
    }

    internal static void ValidateCommit(AgentRun current, HarnessCommit commit)
    {
        if (commit.NextRun.SessionId != current.SessionId
            || commit.NextRun.WorkspaceId != current.WorkspaceId
            || commit.NextRun.Preset != current.Preset
            || commit.NextRun.PolicySnapshotVersion != current.PolicySnapshotVersion
            || !string.Equals(
                commit.NextRun.PolicySnapshotSha256,
                current.PolicySnapshotSha256,
                StringComparison.Ordinal)
            || commit.NextRun.CreatedAt != current.CreatedAt)
        {
            throw new HarnessStoreException("run_projection_identity_changed");
        }

        if (commit.NextRun.LastSequence != current.LastSequence + commit.Events.Count)
        {
            throw new HarnessStoreException("run_sequence_mismatch");
        }

        ValidateIntentSet(
            commit.Events,
            commit.Payloads,
            commit.ConversationOutbox,
            current.SessionId,
            nameof(commit));
    }

    internal static async Task InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewAgentRun request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
                terminal_error_code)
            VALUES (
                $run_id,
                $session_id,
                $workspace_id,
                $state,
                $history_wait_kind,
                $version,
                $last_sequence,
                $budget_window,
                $model_steps,
                $tool_calls,
                $preset,
                $session_preset_version,
                $policy_version,
                $policy_hash,
                $idempotency_key,
                $request_hash,
                $created_at,
                $updated_at,
                $terminal_at,
                $terminal_error)
            """;
        HarnessSqliteMapper.AddRunProjectionParameters(command, request.Run);
        command.Parameters.AddWithValue(
            "$session_preset_version",
            request.CreationIdentity.SessionPresetVersion);
        command.Parameters.AddWithValue(
            "$idempotency_key",
            request.CreationIdentity.IdempotencyKeySha256);
        command.Parameters.AddWithValue(
            "$request_hash",
            request.CreationIdentity.NormalizedRequestSha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> UpdateRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HarnessCommit commit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_runs
            SET state = $state,
                history_wait_kind = $history_wait_kind,
                version = $version,
                last_sequence = $last_sequence,
                budget_window = $budget_window,
                model_steps_in_window = $model_steps,
                tool_calls_in_window = $tool_calls,
                updated_at_utc = $updated_at,
                terminal_at_utc = $terminal_at,
                terminal_error_code = $terminal_error
            WHERE run_id = $run_id
              AND version = $expected_version
            """;
        HarnessSqliteMapper.AddRunProjectionParameters(command, commit.NextRun);
        command.Parameters.AddWithValue("$expected_version", commit.ExpectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal static async Task InsertPayloadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        foreach (var payload in payloads)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO agent_payloads(
                    payload_id,
                    run_id,
                    kind,
                    audience,
                    encoding,
                    byte_cap,
                    protected_content,
                    protection_version,
                    created_at_utc)
                VALUES (
                    $payload_id,
                    $run_id,
                    $kind,
                    $audience,
                    $encoding,
                    $byte_cap,
                    $content,
                    0,
                    $created_at)
                """;
            command.Parameters.AddWithValue("$payload_id", payload.PayloadId.Value);
            command.Parameters.AddWithValue("$run_id", runId.Value);
            command.Parameters.AddWithValue("$kind", (int)payload.Kind);
            command.Parameters.AddWithValue("$audience", (int)payload.Audience);
            command.Parameters.AddWithValue("$encoding", (int)payload.Encoding);
            command.Parameters.AddWithValue("$byte_cap", payload.ByteCap);
            command.Parameters.AddWithValue("$content", payload.Plaintext.ToArray());
            command.Parameters.AddWithValue("$created_at", HarnessSqliteMapper.Utc(occurredAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task InsertEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        long firstSequence,
        IReadOnlyList<HarnessEventIntent> events,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < events.Count; index++)
        {
            var intent = events[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO agent_events(
                    event_id,
                    run_id,
                    sequence,
                    kind,
                    occurred_at_utc,
                    payload_id,
                    model_step_id,
                    tool_call_id,
                    execution_id,
                    approval_id,
                    safe_count,
                    safe_code)
                VALUES (
                    $event_id,
                    $run_id,
                    $sequence,
                    $kind,
                    $occurred_at,
                    $payload_id,
                    $model_step_id,
                    $tool_call_id,
                    $execution_id,
                    $approval_id,
                    $safe_count,
                    $safe_code)
                """;
            command.Parameters.AddWithValue("$event_id", intent.EventId.Value);
            command.Parameters.AddWithValue("$run_id", runId.Value);
            command.Parameters.AddWithValue("$sequence", checked(firstSequence + index));
            command.Parameters.AddWithValue("$kind", HarnessSqliteMapper.EventKind(intent.Kind));
            command.Parameters.AddWithValue("$occurred_at", HarnessSqliteMapper.Utc(occurredAt));
            AddOptional(command, "$payload_id", intent.PayloadId?.Value);
            AddOptional(command, "$model_step_id", intent.ModelStepId?.Value);
            AddOptional(command, "$tool_call_id", intent.ToolCallId?.Value);
            AddOptional(command, "$execution_id", intent.ExecutionId?.Value);
            AddOptional(command, "$approval_id", intent.ApprovalId?.Value);
            command.Parameters.AddWithValue(
                "$safe_count",
                intent.SafeCount is { } safeCount ? safeCount : DBNull.Value);
            AddOptional(command, "$safe_code", intent.SafeCode);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task InsertOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        IReadOnlyList<ConversationOutboxIntent> outbox,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        foreach (var intent in outbox)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO agent_conversation_outbox(
                    intent_id,
                    run_id,
                    session_id,
                    payload_id,
                    role,
                    history_wait_kind,
                    created_at_utc)
                VALUES (
                    $intent_id,
                    $run_id,
                    $session_id,
                    $payload_id,
                    $role,
                    $history_wait_kind,
                    $created_at)
                """;
            command.Parameters.AddWithValue("$intent_id", intent.IntentId.Value);
            command.Parameters.AddWithValue("$run_id", runId.Value);
            command.Parameters.AddWithValue("$session_id", intent.SessionId.Value);
            command.Parameters.AddWithValue("$payload_id", intent.PayloadId.Value);
            command.Parameters.AddWithValue("$role", (int)intent.Role);
            command.Parameters.AddWithValue("$history_wait_kind", (int)intent.HistoryWaitKind);
            command.Parameters.AddWithValue("$created_at", HarnessSqliteMapper.Utc(occurredAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task DeleteRunRulesIfTerminalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (!run.State.IsTerminal())
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM agent_run_rules WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", run.RunId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateIntentSet(
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<ConversationOutboxIntent> outbox,
        SessionId expectedSessionId,
        string parameterName)
    {
        if (events.Any(item => item is null)
            || payloads.Any(item => item is null)
            || outbox.Any(item => item is null))
        {
            throw new ArgumentException("A write set cannot contain null intents.", parameterName);
        }

        var eventAndOutboxIds = events.Select(item => item.EventId)
            .Concat(outbox.Select(item => item.IntentId))
            .ToArray();
        if (eventAndOutboxIds.Distinct().Count() != eventAndOutboxIds.Length
            || payloads.Select(item => item.PayloadId).Distinct().Count() != payloads.Count)
        {
            throw new ArgumentException("A write set cannot contain duplicate IDs.", parameterName);
        }

        var payloadIds = payloads.Select(item => item.PayloadId).ToHashSet();
        if (events.Any(item => item.PayloadId is { } id && !payloadIds.Contains(id))
            || outbox.Any(item => !payloadIds.Contains(item.PayloadId)))
        {
            throw new ArgumentException(
                "Event and outbox payloads must be in the same write set.",
                parameterName);
        }

        if (outbox.Any(item => item.SessionId != expectedSessionId))
        {
            throw new HarnessStoreException("outbox_session_mismatch");
        }
    }

    private static void AddOptional(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
}
