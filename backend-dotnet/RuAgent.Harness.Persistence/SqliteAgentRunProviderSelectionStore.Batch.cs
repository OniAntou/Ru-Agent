using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteAgentRunProviderSelectionStore
{
    private const int MaximumBatchRunIds = 100;

    /// <summary>Reads one bounded page of pinned metadata, preserving the single-run reader's missing and delegated states.</summary>
    public async Task<IReadOnlyDictionary<RunId, AgentRunProviderSelectionState>> GetManyAsync(
        IReadOnlyCollection<RunId> runIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        if (runIds.Count > MaximumBatchRunIds) throw new ArgumentOutOfRangeException(nameof(runIds));
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<RunId, AgentRunProviderSelectionState>();
        if (runIds.Count == 0) return result;

        var ids = runIds.Distinct().ToArray();
        foreach (var id in ids) _ = id.Value;
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var parameters = ids.Select((_, index) => $"$run_{index}").ToArray();
        command.CommandText = $"""
            SELECT {SelectionColumns}, run_id
            FROM agent_run_provider_selections
            WHERE run_id IN ({string.Join(", ", parameters)})
                AND (backend IS NOT NULL OR (
                    NOT EXISTS(SELECT 1 FROM agent_internal_runs
                        WHERE run_id = agent_run_provider_selections.run_id)
                    AND NOT EXISTS(SELECT 1 FROM agent_subagent_attempts
                        WHERE run_id = agent_run_provider_selections.run_id)))
            """;
        for (var index = 0; index < ids.Length; index++)
            command.Parameters.AddWithValue(parameters[index], ids[index].Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var runIdColumn = reader.GetOrdinal("run_id");
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new RunId(reader.GetString(runIdColumn)), ReadSelectionState(reader));
        return result;
    }
}
