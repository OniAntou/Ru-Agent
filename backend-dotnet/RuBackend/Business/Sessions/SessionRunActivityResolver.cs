using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;

namespace RuBackend.Business.Sessions;

/// <summary>One bounded, indexed read for sidebar run activity, including background sessions.</summary>
public static class SessionRunActivityResolver
{
    public static string BuildSql(int count)
    {
        if (count is < 1 or > SessionWorkspaceResolver.MaximumRequestedSessions)
            throw new ArgumentOutOfRangeException(nameof(count));
        var parameters = string.Join(",", Enumerable.Range(0, count).Select(index => $"$session_{index}"));
        return $"""
            SELECT session_id FROM agent_runs
            WHERE terminal_at_utc IS NULL AND session_id IN ({parameters})
                AND state IN ($created, $history, $running, $subagents)
            """;
    }

    public static async Task<HashSet<string>> ResolveAsync(
        IReadOnlyList<string> sessionIds, HarnessSqliteOptions options, CancellationToken cancellationToken)
    {
        var empty = new HashSet<string>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return empty;

        try
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            await using var connection = await SessionWorkspaceResolver
                .OpenHarnessConnectionAsync(options, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = BuildSql(sessionIds.Count);
            for (int index = 0; index < sessionIds.Count; index++)
                command.Parameters.AddWithValue($"$session_{index}", sessionIds[index]);
            command.Parameters.AddWithValue("$created", (int)HarnessRunState.Created);
            command.Parameters.AddWithValue("$history", (int)HarnessRunState.WaitingHistory);
            command.Parameters.AddWithValue("$running", (int)HarnessRunState.Running);
            command.Parameters.AddWithValue("$subagents", (int)HarnessRunState.WaitingSubagents);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The activity marks are decorative: a locked or unreadable harness
            // database must not fail the session list that carries them.
            return empty;
        }
    }
}
