using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed record SubagentContinuationCandidate(RunId RootRunId, string AgentId, RunId PreviousRunId);

public sealed partial class SqliteSubagentStore
{
    public async Task<IReadOnlyList<SubagentContinuationCandidate>> ListContinuationCandidatesAsync(string? afterAgentId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.root_run_id,s.agent_id,a.run_id FROM agent_subagents s
            JOIN agent_subagent_families f ON f.root_run_id=s.root_run_id
            JOIN agent_runs root ON root.run_id=s.root_run_id
            JOIN agent_subagent_attempts a ON a.agent_id=s.agent_id JOIN agent_runs child ON child.run_id=a.run_id
            LEFT JOIN agent_subagent_budgets b ON b.root_run_id=root.run_id AND b.window=root.budget_window
            WHERE s.canceled=0 AND f.canceled=0 AND root.terminal_at_utc IS NULL AND child.state IN($completed,$failed)
                AND s.agent_id>$after AND COALESCE(b.model_steps,root.model_steps_in_window)<128
                AND NOT EXISTS(SELECT 1 FROM agent_subagent_attempts newer WHERE newer.agent_id=s.agent_id AND newer.attempt_number>a.attempt_number)
                AND EXISTS(SELECT 1 FROM agent_subagent_messages m WHERE m.agent_id=s.agent_id
                    AND m.consumed_run_id IS NULL AND m.continuation_run_id IS NULL AND m.continuation_error_code IS NULL)
            ORDER BY s.agent_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$completed", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$failed", (int)HarnessRunState.Failed);
        command.Parameters.AddWithValue("$after", afterAgentId ?? "");
        command.Parameters.AddWithValue("$limit", FamilyPageSize);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<SubagentContinuationCandidate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(new(new(reader.GetString(0)), reader.GetString(1), new(reader.GetString(2))));
        return result.AsReadOnly();
    }

    public async Task RecordContinuationFailureAsync(SubagentContinuationCandidate candidate, string code, CancellationToken ct)
    {
        _ = new HarnessStoreException(code); // Keep persisted diagnostics bounded tokens.
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_subagent_messages SET continuation_error_code=$code
            WHERE agent_id=$agent AND consumed_run_id IS NULL AND continuation_run_id IS NULL AND continuation_error_code IS NULL
                AND EXISTS(SELECT 1 FROM agent_subagents s JOIN agent_subagent_attempts a ON a.agent_id=s.agent_id
                    WHERE s.agent_id=$agent AND s.root_run_id=$root AND a.run_id=$previous
                    AND NOT EXISTS(SELECT 1 FROM agent_subagent_attempts newer WHERE newer.agent_id=s.agent_id AND newer.attempt_number>a.attempt_number))
            """;
        command.Parameters.AddWithValue("$agent", candidate.AgentId);
        command.Parameters.AddWithValue("$root", candidate.RootRunId.Value);
        command.Parameters.AddWithValue("$previous", candidate.PreviousRunId.Value);
        command.Parameters.AddWithValue("$code", code);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
