using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentSnapshotReader
{
    private static async Task<IReadOnlyList<SubagentResultSnapshot>> ReadResultsAsync(SqliteConnection connection,
        SqliteTransaction transaction, SubagentRecord agent, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT v.result_version,v.run_id,o.state,o.code,v.payload_id,v.delivered,a.attempt_number,r.state,
                j.status,j.applied_files,j.code
            FROM agent_subagent_results v JOIN agent_runs r ON r.run_id=v.run_id
            LEFT JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
            LEFT JOIN agent_subagent_attempts a ON a.agent_id=v.agent_id AND a.run_id=v.run_id
            LEFT JOIN agent_subagent_apply_operations j ON j.root_run_id=$root AND j.agent_id=v.agent_id AND j.result_version=v.result_version
            WHERE v.agent_id=$agent ORDER BY v.result_version LIMIT 4
            """;
        command.Parameters.AddWithValue("$root", agent.RootRunId.Value);
        command.Parameters.AddWithValue("$agent", agent.AgentId);
        var results = new List<(SubagentResult Result, SubagentApplyOutcome? Apply)>();
        using (var row = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await row.ReadAsync(ct).ConfigureAwait(false))
            {
                if (results.Count >= SubagentLimits.MaxAttempts || row.IsDBNull(2) || row.IsDBNull(6)) Invalid();
                SubagentResultRow.Validate(row.GetInt64(0), row.GetInt64(6), (HarnessRunState)row.GetInt32(2), (HarnessRunState)row.GetInt32(7));
                var value = new SubagentResult(agent.AgentId, row.GetInt64(0), new(row.GetString(1)), (HarnessRunState)row.GetInt32(2),
                    row.IsDBNull(3) ? null : row.GetString(3), new(row.GetString(4)), row.GetInt32(5) != 0);
                SubagentApplyOutcome? applied = row.IsDBNull(8) ? null : new((SubagentApplyStatus)row.GetInt32(8), row.GetInt32(9), row.GetString(10));
                if (applied is not null && (!Enum.IsDefined(applied.Status) || applied.AppliedFiles is < 0 or > SubagentLimits.MaxSnapshotFiles)) Invalid();
                results.Add((value, applied));
            }
        }
        var snapshots = new List<SubagentResultSnapshot>();
        foreach (var (result, apply) in results)
            snapshots.Add(new(result, apply, await SqliteSubagentApplyJournal.HasResolutionAsync(connection, transaction,
                agent.RootRunId.Value, agent.AgentId, result.Version, ct).ConfigureAwait(false)));
        return snapshots.AsReadOnly();
    }
}
