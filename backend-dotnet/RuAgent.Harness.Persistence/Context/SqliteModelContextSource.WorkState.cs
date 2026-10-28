using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource
{
    private async Task AppendSessionWorkStateAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        RunId runId,
        List<ModelContextEntry> sessionBase,
        CancellationToken cancellationToken)
    {
        if (_workStates is null) return;
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new HarnessStoreException("run_not_found");
        var snapshot = await _workStates.GetSnapshotAsync(run.SessionId, run.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null) return;
        var sequence = sessionBase.Count == 0 ? 1 : checked(sessionBase.Max(entry => entry.Sequence) + 1);
        sessionBase.Add(ModelContextEntry.Text(
            SessionWorkState.ContextSourceId, sequence, ModelContextRole.User,
            SessionWorkStateRenderer.Render(snapshot.State, snapshot.HasUnrepresentedReceipts,
                await VisibleIntentRunsAsync(connection, transaction, run, snapshot.State, cancellationToken).ConfigureAwait(false))));
    }


    private static async Task<IReadOnlySet<string>?> VisibleIntentRunsAsync(SqliteConnection connection,
        SqliteTransaction transaction, AgentRun run, SessionWorkState state, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM business_sessions WHERE session_id=$session";
        command.Parameters.AddWithValue("$session", run.SessionId.Value);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return null;
        var candidates = state.UserConstraints.Concat(state.EarlierUserIntent).Append(state.UserGoal)
            .Select(f => f.SourceRunId).Distinct(StringComparer.Ordinal).ToArray();
        var parameters = candidates.Select((_, index) => "$source" + index).ToArray();
        for (var index = 0; index < candidates.Length; index++) command.Parameters.AddWithValue(parameters[index], candidates[index]);
        command.CommandText = $"""
            SELECT DISTINCT source_run_id FROM business_session_messages
            WHERE session_id=$session AND role='user' AND source_run_id IN ({string.Join(',', parameters)})
            """;
        var visible = new HashSet<string>(StringComparer.Ordinal) { run.RunId.Value };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) visible.Add(reader.GetString(0));
        return visible;
    }
}
