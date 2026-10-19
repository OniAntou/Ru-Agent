using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    /// <summary>Commits the single identity's tombstone before returning its exact owned attempts for runtime cancellation.</summary>
    public async Task<IReadOnlyList<RunId>> CancelAsync(RunId rootRunId, string agentId, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(agentId)) throw new HarnessStoreException("subagent_arguments_invalid");
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        command.CommandText = "UPDATE agent_subagents SET canceled=1 WHERE root_run_id=$root AND agent_id=$agent";
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new HarnessStoreException("subagent_not_found");
        command.CommandText = """
            SELECT a.run_id,i.root_run_id FROM agent_subagent_attempts a
            LEFT JOIN agent_internal_runs i ON i.run_id=a.run_id
            WHERE a.agent_id=$agent ORDER BY a.attempt_number LIMIT 4
            """;
        var runs = new List<RunId>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1) || reader.GetString(1) != rootRunId.Value || runs.Count == SubagentLimits.MaxAttempts)
                    throw new HarnessStoreException("subagent_cancel_ownership_incomplete");
                runs.Add(new(reader.GetString(0)));
            }
        }
        if (runs.Count == 0) throw new HarnessStoreException("subagent_cancel_ownership_incomplete");
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return runs.AsReadOnly();
    }
}
