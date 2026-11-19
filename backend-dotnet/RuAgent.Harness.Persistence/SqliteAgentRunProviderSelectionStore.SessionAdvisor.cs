using System.Globalization;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

/// <summary>
/// The advisor a session's new root runs pin (ADR 0047, amended): one row per session,
/// none for a session without an advisor. A change applies to the session's next run only;
/// a run that already pinned keeps its own copy in its provider selection.
/// </summary>
public sealed partial class SqliteAgentRunProviderSelectionStore
{
    public async Task<RunProviderIntent?> GetSessionAdvisorAsync(string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT backend,model FROM agent_session_advisors WHERE session_id=$session";
        command.Parameters.AddWithValue("$session", sessionId);
        return await ReadAdvisorAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>The advisor of the session that owns <paramref name="runId"/>, read when the run pins.</summary>
    public async Task<RunProviderIntent?> GetRunSessionAdvisorAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.backend,a.model FROM agent_runs r
            JOIN agent_session_advisors a ON a.session_id=r.session_id
            WHERE r.run_id=$run
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        return await ReadAdvisorAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>Selects the session's advisor, or clears it when <paramref name="advisor"/> is null.</summary>
    public async Task SetSessionAdvisorAsync(string sessionId, RunProviderIntent? advisor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (advisor is null)
        {
            command.CommandText = "DELETE FROM agent_session_advisors WHERE session_id=$session";
        }
        else
        {
            command.CommandText = """
                INSERT INTO agent_session_advisors(session_id,backend,model,updated_at_utc)
                VALUES($session,$backend,$model,$updated)
                ON CONFLICT(session_id) DO UPDATE SET
                    backend=excluded.backend, model=excluded.model, updated_at_utc=excluded.updated_at_utc
                """;
            command.Parameters.AddWithValue("$backend", advisor.Backend);
            command.Parameters.AddWithValue("$model", advisor.Model);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<RunProviderIntent?> ReadAdvisorAsync(SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        try { return new RunProviderIntent(reader.GetString(0), reader.GetString(1)); }
        catch (ArgumentException error) { throw new HarnessStoreException("session_advisor_invalid", error); }
    }
}
