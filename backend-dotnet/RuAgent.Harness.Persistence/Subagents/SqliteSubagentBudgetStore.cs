using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed class SqliteSubagentBudgetStore(HarnessSqliteOptions options)
{
    public Task<bool> ReserveAsync(RunId rootRunId, int window, string invocationId, SubagentBudgetKind kind,
        int priorRootModelSteps, int priorRootToolCalls, CancellationToken ct) =>
        ReserveAmountAsync(rootRunId, window, invocationId, kind, 1, priorRootModelSteps, priorRootToolCalls, ct);

    public async Task<bool> ReserveAmountAsync(RunId rootRunId, int window, string invocationId, SubagentBudgetKind kind, int amount,
        int priorRootModelSteps, int priorRootToolCalls, CancellationToken ct)
    {
        if (window < 1 || !SubagentContractValidator.IsValidId(invocationId) || !Enum.IsDefined(kind)
            || priorRootModelSteps < 0 || priorRootToolCalls < 0 || amount < 1 || amount > SubagentLimits.FamilyToolCalls
            || kind == SubagentBudgetKind.ModelStep && amount != 1) throw new HarnessStoreException("subagent_budget_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootRunId, ct).ConfigureAwait(false);
        if (root is null || root.Status.State.IsTerminal() || root.BudgetWindow != window) return false;
        priorRootModelSteps = Math.Max(priorRootModelSteps, root.ModelStepsInWindow);
        priorRootToolCalls = Math.Max(priorRootToolCalls, root.ToolCallsInWindow);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        command.Parameters.AddWithValue("$window", window);
        command.Parameters.AddWithValue("$invocation", invocationId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$amount", amount);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1) OR EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$root)";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0) return false;
        command.CommandText = "SELECT kind,amount FROM agent_subagent_reservations WHERE root_run_id=$root AND window=$window AND invocation_id=$invocation";
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetInt32(0) != (int)kind || reader.GetInt32(1) != amount)
                    throw new HarnessStoreException("subagent_reservation_conflict");
                return true;
            }
        }
        if (priorRootModelSteps > SubagentLimits.FamilyModelSteps || priorRootToolCalls > SubagentLimits.FamilyToolCalls) return false;
        command.Parameters.AddWithValue("$steps", priorRootModelSteps);
        command.Parameters.AddWithValue("$tools", priorRootToolCalls);
        command.CommandText = """
            INSERT INTO agent_subagent_budgets(root_run_id,window,model_steps,tool_calls) VALUES($root,$window,$steps,$tools)
            ON CONFLICT DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = kind == SubagentBudgetKind.ModelStep
            ? "UPDATE agent_subagent_budgets SET model_steps=model_steps+$amount WHERE root_run_id=$root AND window=$window AND model_steps<=128-$amount"
            : "UPDATE agent_subagent_budgets SET tool_calls=tool_calls+$amount WHERE root_run_id=$root AND window=$window AND tool_calls<=256-$amount";
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return false;
        command.CommandText = "INSERT INTO agent_subagent_reservations(root_run_id,window,invocation_id,kind,amount) VALUES($root,$window,$invocation,$kind,$amount)";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
