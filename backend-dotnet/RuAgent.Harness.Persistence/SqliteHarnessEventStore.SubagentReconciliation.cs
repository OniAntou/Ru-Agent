using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore
{
    private async Task SaveSubagentReconciliationAsync(SqliteConnection connection, SqliteTransaction transaction,
        AgentRun current, HarnessCommit commit, CancellationToken ct)
    {
        var decisions = commit.Events.Where(e => e.Kind == HarnessEventKind.ReconciliationResolved
            && e.SafeCode is "applied" or "not_applied" or "unknown_continue").ToArray();
        if (decisions.Length == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", current.RunId.Value);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_apply_operations WHERE root_run_id=$root) OR EXISTS(SELECT 1 FROM agent_subagent_user_applies WHERE root_run_id=$root)";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) == 0) return;
        if (decisions.Length != 1 || commit.Events.Count != 1 || current.State != HarnessRunState.ReconciliationRequired
            || commit.NextRun.State != HarnessRunState.Running)
            throw new HarnessStoreException("subagent_reconciliation_transition_invalid");
        var resolution = decisions[0];
        var arguments = await ReadSubagentReconciliationArgumentsAsync(connection, transaction, current.RunId, resolution, ct)
            .ConfigureAwait(false);
        if (arguments is null || arguments.ToolId != "agent_apply") return;
        if (arguments.ToolVersion != "1.0.0" || !SubagentContractValidator.Validate("agent_apply", arguments.Arguments).IsValid)
            throw new HarnessStoreException("subagent_reconciliation_call_invalid");
        command.CommandText = """
            INSERT INTO agent_subagent_apply_resolutions(root_run_id,agent_id,result_version,resolution_event_id,
                model_step_id,tool_call_id,execution_id,decision)
            SELECT o.root_run_id,o.agent_id,o.result_version,$event,$step,$call,$execution,$decision
            FROM agent_subagent_apply_operations o JOIN agent_subagents s ON s.agent_id=o.agent_id AND s.root_run_id=o.root_run_id
            WHERE o.root_run_id=$root AND o.agent_id=$agent AND o.result_version=$version AND s.role=$coding
                AND o.status IN ($pending,$partial,$unknown)
            ON CONFLICT(root_run_id,agent_id,result_version) DO NOTHING
            """;
        command.Parameters.AddWithValue("$agent", arguments.Arguments.GetProperty("agent_id").GetString()!);
        command.Parameters.AddWithValue("$version", arguments.Arguments.GetProperty("result_version").GetInt64());
        command.Parameters.AddWithValue("$event", resolution.EventId.Value);
        command.Parameters.AddWithValue("$step", resolution.ModelStepId!.Value.Value);
        command.Parameters.AddWithValue("$call", resolution.ToolCallId!.Value.Value);
        command.Parameters.AddWithValue("$execution", resolution.ExecutionId!.Value.Value);
        command.Parameters.AddWithValue("$decision", resolution.SafeCode!);
        command.Parameters.AddWithValue("$coding", (int)SubagentRole.Coding);
        command.Parameters.AddWithValue("$pending", (int)SubagentApplyStatus.Pending);
        command.Parameters.AddWithValue("$partial", (int)SubagentApplyStatus.Partial);
        command.Parameters.AddWithValue("$unknown", (int)SubagentApplyStatus.Unknown);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = """
            UPDATE agent_subagent_user_applies SET status='unknown',approval_id=NULL,code='subagent_apply_user_reconciled',revision=revision+1
            WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version
                AND 'step_'||action_key=$step AND 'call_'||action_key=$call AND 'exec_'||action_key=$execution
                AND status IN ('executing','unknown')
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
