using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentResultStore(HarnessSqliteOptions options, HarnessPayloadStore payloads)
{
    public const int PublicationPageSize = 128;

    public async Task<IReadOnlyList<SubagentPublicationCandidate>> ListUnpublishedAsync(string? afterRunId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.agent_id,s.root_run_id,r.run_id,s.role,r.state FROM agent_subagent_attempts a
            JOIN agent_subagents s ON s.agent_id=a.agent_id JOIN agent_runs r ON r.run_id=a.run_id
            LEFT JOIN agent_subagent_results v ON v.agent_id=a.agent_id AND v.run_id=a.run_id
            WHERE r.terminal_at_utc IS NOT NULL AND v.result_version IS NULL AND r.run_id>$after
            ORDER BY r.run_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", afterRunId ?? "");
        command.Parameters.AddWithValue("$limit", PublicationPageSize);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<SubagentPublicationCandidate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(new(reader.GetString(0), new(reader.GetString(1)), new(reader.GetString(2)),
                (SubagentRole)reader.GetInt32(3), (HarnessRunState)reader.GetInt32(4)));
        return results.AsReadOnly();
    }

    public async Task<IReadOnlyList<SubagentResult>> ListAsync(RunId rootId, string agentId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.result_version,v.run_id,o.state,o.code,v.payload_id,v.delivered,a.attempt_number,r.state
            FROM agent_subagent_results v JOIN agent_subagents s ON s.agent_id=v.agent_id JOIN agent_runs r ON r.run_id=v.run_id
            LEFT JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
            LEFT JOIN agent_subagent_attempts a ON a.agent_id=v.agent_id AND a.run_id=v.run_id
            WHERE s.root_run_id=$root AND s.agent_id=$agent ORDER BY v.result_version LIMIT 3
            """;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<SubagentResult>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2)) throw new HarnessStoreException("subagent_result_outcome_missing");
            SubagentResultRow.Validate(reader.GetInt64(0), reader.IsDBNull(6) ? 0 : reader.GetInt64(6), (HarnessRunState)reader.GetInt32(2), (HarnessRunState)reader.GetInt32(7));
            results.Add(new(agentId, reader.GetInt64(0), new(reader.GetString(1)), (HarnessRunState)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), new(reader.GetString(4)), reader.GetInt32(5) != 0));
        }
        return results.AsReadOnly();
    }
}
