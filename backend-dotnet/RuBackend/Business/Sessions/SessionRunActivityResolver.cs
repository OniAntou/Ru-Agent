using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;

namespace RuBackend.Business.Sessions;

/// <summary>What a session's non-terminal root run is doing, as the session list reports it.</summary>
public enum SessionRunActivity
{
    /// <summary>Created, loading history, stepping or waiting on its subagents.</summary>
    Working,
    WaitingApproval,
    /// <summary>Parked on an <c>ask_user</c> question.</summary>
    WaitingAnswer,
    /// <summary>Parked on the user for another reason (budget or transient-provider wait).</summary>
    WaitingUser,
    WaitingWorkspaceTrust,
}

/// <summary>One bounded, indexed read for sidebar run activity, including background sessions.</summary>
public static class SessionRunActivityResolver
{
    public static string BuildSql(int count)
    {
        if (count is < 1 or > SessionWorkspaceResolver.MaximumRequestedSessions)
            throw new ArgumentOutOfRangeException(nameof(count));
        var parameters = string.Join(",", Enumerable.Range(0, count).Select(index => $"$session_{index}"));
        // A parked run's newest event is its park cause; only a WaitingUser run reads it,
        // through the (run_id, sequence DESC) index, to tell a question from other waits.
        return $"""
            SELECT run.session_id, run.state,
                CASE WHEN run.state = $user THEN (
                    SELECT event.kind FROM agent_events AS event
                    WHERE event.run_id = run.run_id ORDER BY event.sequence DESC LIMIT 1)
                END
            FROM agent_runs AS run
            WHERE run.terminal_at_utc IS NULL AND run.session_id IN ({parameters})
                AND run.state IN ($created, $trust, $history, $running, $approval, $user, $subagents)
            """;
    }

    /// <summary>
    /// The <c>agent_wait</c> wire value: which user action a waiting run needs, or null
    /// while it is working.
    /// </summary>
    public static string? WaitWireValue(SessionRunActivity activity) => activity switch
    {
        SessionRunActivity.WaitingApproval => "approval",
        SessionRunActivity.WaitingAnswer => "question",
        SessionRunActivity.WaitingUser => "user",
        SessionRunActivity.WaitingWorkspaceTrust => "workspace_trust",
        _ => null,
    };

    public static async Task<IReadOnlyDictionary<string, SessionRunActivity>> ResolveAsync(
        IReadOnlyList<string> sessionIds, HarnessSqliteOptions options, CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, SessionRunActivity>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return empty;

        try
        {
            var result = new Dictionary<string, SessionRunActivity>(StringComparer.Ordinal);
            await using var connection = await SessionWorkspaceResolver
                .OpenHarnessConnectionAsync(options, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = BuildSql(sessionIds.Count);
            for (int index = 0; index < sessionIds.Count; index++)
                command.Parameters.AddWithValue($"$session_{index}", sessionIds[index]);
            command.Parameters.AddWithValue("$created", (int)HarnessRunState.Created);
            command.Parameters.AddWithValue("$trust", (int)HarnessRunState.WaitingWorkspaceTrust);
            command.Parameters.AddWithValue("$history", (int)HarnessRunState.WaitingHistory);
            command.Parameters.AddWithValue("$running", (int)HarnessRunState.Running);
            command.Parameters.AddWithValue("$approval", (int)HarnessRunState.WaitingApproval);
            command.Parameters.AddWithValue("$user", (int)HarnessRunState.WaitingUser);
            command.Parameters.AddWithValue("$subagents", (int)HarnessRunState.WaitingSubagents);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var activity = Classify((HarnessRunState)reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
                // A session has at most one non-terminal run; should two ever be read, a
                // wait on the user outranks work so it is never hidden.
                string sessionId = reader.GetString(0);
                if (!result.TryGetValue(sessionId, out var existing) || existing == SessionRunActivity.Working)
                    result[sessionId] = activity;
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The activity marks are decorative: a locked or unreadable harness
            // database must not fail the session list that carries them.
            return empty;
        }
    }

    private static SessionRunActivity Classify(HarnessRunState state, string? lastEventKind) => state switch
    {
        HarnessRunState.WaitingApproval => SessionRunActivity.WaitingApproval,
        HarnessRunState.WaitingUser => string.Equals(lastEventKind, "input.requested", StringComparison.Ordinal)
            ? SessionRunActivity.WaitingAnswer
            : SessionRunActivity.WaitingUser,
        HarnessRunState.WaitingWorkspaceTrust => SessionRunActivity.WaitingWorkspaceTrust,
        _ => SessionRunActivity.Working,
    };
}
