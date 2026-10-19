using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    public async Task<SubagentAttempt?> CreateContinuationAsync(RunId rootId, string agentId, RunId expectedPrevious,
        NewAgentRun next, CancellationToken ct)
    {
        HarnessSqliteWriteSet.ValidateNewRun(next);
        if (next.ConversationOutbox.Count != 0 || next.Run.State != HarnessRunState.Created
            || next.Run.ModelStepsInWindow != 0 || next.Run.ToolCallsInWindow != 0 || next.Run.BudgetWindow != 1)
            throw new HarnessStoreException("subagent_continuation_invalid");
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = await ReadLatestAttemptAsync(connection, transaction, rootId, agentId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootId, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.CommandText = "SELECT canceled FROM agent_subagent_families WHERE root_run_id=$root";
        if (root is null || root.State.IsTerminal() || previous.Agent.Canceled
            || (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 1L) != 0)
            throw new HarnessStoreException("subagent_root_inactive");
        if (previous.Run.RunId != expectedPrevious || !previous.Run.State.IsTerminal()) return previous;
        if (previous.Run.State is not (HarnessRunState.Completed or HarnessRunState.Failed))
            throw new HarnessStoreException("subagent_continuation_denied");
        command.Parameters.AddWithValue("$agent", agentId);
        if (previous.Agent.Role == SubagentRole.Coding && previous.Run.State == HarnessRunState.Completed)
        {
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM agent_subagent_results v
                    LEFT JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
                    WHERE v.agent_id=$agent AND v.run_id=$previous AND v.result_version=$number
                    AND ((o.state=8 AND o.code IS NOT NULL) OR (COALESCE(o.state,7)=7
                        AND EXISTS(SELECT 1 FROM agent_subagent_change_sets c WHERE c.run_id=v.run_id))))
                """;
            command.Parameters.AddWithValue("$previous", previous.Run.RunId.Value);
            command.Parameters.AddWithValue("$number", previous.AttemptNumber);
            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) == 0)
                throw new HarnessStoreException("subagent_result_pending");
        }
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_messages WHERE agent_id=$agent AND consumed_run_id IS NULL AND continuation_run_id IS NULL AND continuation_error_code IS NULL)";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) == 0) return null;
        if (previous.AttemptNumber >= SubagentLimits.MaxAttempts) throw new HarnessStoreException("subagent_attempt_limit");
        ValidateContinuationIdentity(previous.Run, next);
        if (await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, root.SessionId, ct).ConfigureAwait(false)
            || await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, next.Run.SessionId, ct).ConfigureAwait(false))
            throw new HarnessStoreException("session_deleted");
        if (root.ModelStepsInWindow > SubagentLimits.FamilyModelSteps || root.ToolCallsInWindow > SubagentLimits.FamilyToolCalls)
            throw new HarnessStoreException("subagent_family_budget_exhausted");
        command.Parameters.AddWithValue("$window", root.BudgetWindow);
        command.Parameters.AddWithValue("$steps", root.ModelStepsInWindow);
        command.Parameters.AddWithValue("$tools", root.ToolCallsInWindow);
        command.CommandText = """
            INSERT INTO agent_subagent_budgets(root_run_id,window,model_steps,tool_calls)
            VALUES($root,$window,$steps,$tools) ON CONFLICT DO NOTHING;
            SELECT model_steps FROM agent_subagent_budgets WHERE root_run_id=$root AND window=$window;
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 128L) >= SubagentLimits.FamilyModelSteps)
            throw new HarnessStoreException("subagent_family_budget_exhausted");
        await HarnessSqliteWriteSet.InsertRunAsync(connection, transaction, next, ct).ConfigureAwait(false);
        await MarkInternalRunAsync(connection, transaction, next.Run.RunId, rootId, ct).ConfigureAwait(false);
        await SqliteAgentRunProviderSelectionStore.CopyPinnedAsync(connection, transaction, previous.Run.RunId, next.Run.RunId, ct).ConfigureAwait(false);
        await Payloads.InsertAsync(connection, transaction, next.Run.RunId, next.Payloads, next.Events, 1, next.Run.CreatedAt, ct).ConfigureAwait(false);
        await HarnessSqliteWriteSet.InsertEventsAsync(connection, transaction, next.Run.RunId, 1, next.Events, next.Run.CreatedAt, ct).ConfigureAwait(false);
        command.Parameters.AddWithValue("$run", next.Run.RunId.Value);
        command.Parameters.AddWithValue("$attempt", previous.AttemptNumber + 1);
        command.Parameters.AddWithValue("$created", next.Run.CreatedAt.ToUniversalTime().ToString("O"));
        command.CommandText = """
            INSERT INTO agent_subagent_attempts(agent_id,attempt_number,run_id,created_at_utc) VALUES($agent,$attempt,$run,$created);
            UPDATE agent_subagent_messages SET continuation_run_id=$run
                WHERE agent_id=$agent AND consumed_run_id IS NULL AND continuation_run_id IS NULL AND continuation_error_code IS NULL;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(previous.Agent, next.Run, previous.AttemptNumber + 1, next.Run.CreatedAt, null, null);
    }

    private static void ValidateContinuationIdentity(AgentRun previous, NewAgentRun next)
    {
        var run = next.Run;
        if (run.RunId == previous.RunId || run.SessionId != previous.SessionId || run.WorkspaceId != previous.WorkspaceId
            || run.Preset != previous.Preset || run.PermissionProfileVersion != previous.PermissionProfileVersion
            || run.PolicySnapshotVersion != previous.PolicySnapshotVersion || run.PolicySnapshotSha256 != previous.PolicySnapshotSha256
            || run.CreatedAt < previous.UpdatedAt || next.CreationIdentity.SessionPresetVersion != 1)
            throw new HarnessStoreException("subagent_continuation_invalid");
    }
}
