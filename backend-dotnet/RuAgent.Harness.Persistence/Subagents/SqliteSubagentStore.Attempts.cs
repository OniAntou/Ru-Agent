using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    public async Task<RunId?> GetAttemptRunIdAsync(RunId root, string agentId, int attemptNumber, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.run_id FROM agent_subagent_attempts a JOIN agent_subagents s ON s.agent_id=a.agent_id
            WHERE s.root_run_id=$root AND s.agent_id=$agent AND a.attempt_number=$attempt
            """;
        command.Parameters.AddWithValue("$root", root.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$attempt", attemptNumber);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string value ? new RunId(value) : null;
    }

    public async Task<SubagentAttempt?> GetLatestAttemptAsync(RunId rootRunId, string agentId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadLatestAttemptAsync(connection, null, rootRunId, agentId, ct).ConfigureAwait(false);
    }

    public async Task<SubagentRecord?> FindOwnerAsync(SessionId sessionId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_id,root_run_id,internal_session_id,role,canceled FROM agent_subagents WHERE internal_session_id=$session";
        command.Parameters.AddWithValue("$session", sessionId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<bool> TryAdmitAsync(string agentId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.root_run_id FROM agent_subagents s JOIN agent_subagent_families f ON f.root_run_id=s.root_run_id
            JOIN agent_runs r ON r.run_id=s.root_run_id
            WHERE s.agent_id=$agent AND s.canceled=0 AND f.canceled=0 AND r.terminal_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string rootId) return false;
        var attempt = await ReadLatestAttemptAsync(connection, transaction, new RunId(rootId), agentId, ct).ConfigureAwait(false);
        if (attempt is null || attempt.AdmittedAt is not null || attempt.Run.State != HarnessRunState.Created
            || now < attempt.CreatedAt || now - attempt.CreatedAt >= SubagentLimits.AttemptTimeout) return false;
        command.CommandText = """
            SELECT count(*) FROM agent_runs r JOIN agent_run_provider_selections p ON p.run_id=r.run_id
            JOIN agent_subagent_scopes scope ON scope.run_id=r.run_id
            WHERE r.run_id=$run AND r.base_context_captured_at_utc IS NOT NULL AND p.backend IS NOT NULL
            """;
        command.Parameters.AddWithValue("$run", attempt.Run.RunId.Value);
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1) return false;
        command.CommandText = """
            SELECT count(*) FROM agent_subagent_attempts a
            JOIN agent_subagents s ON s.agent_id=a.agent_id JOIN agent_runs r ON r.run_id=a.run_id
            WHERE s.root_run_id=$root AND s.canceled=0 AND a.admitted_at_utc IS NOT NULL AND r.terminal_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$root", rootId);
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) >= SubagentLimits.MaxRunnable) return false;
        command.CommandText = """
            UPDATE agent_subagent_attempts SET admitted_at_utc=$now,deadline_at_utc=$deadline
            WHERE agent_id=$agent AND attempt_number=$attempt AND admitted_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$deadline", now.Add(SubagentLimits.AttemptTimeout).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$attempt", attempt.AttemptNumber);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return false;
        var admission = SubagentAdmission.CreateCommit(attempt.Run, new EventId("evt_" + Guid.NewGuid().ToString("N")), now);
        HarnessSqliteWriteSet.ValidateCommit(attempt.Run, admission);
        if (!await HarnessSqliteWriteSet.UpdateRunAsync(connection, transaction, admission, ct).ConfigureAwait(false))
            return false;
        await HarnessSqliteWriteSet.InsertEventsAsync(connection, transaction, attempt.Run.RunId,
            attempt.Run.LastSequence + 1, admission.Events, now, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<SubagentAttempt?> ReadLatestAttemptAsync(SqliteConnection connection, SqliteTransaction? transaction,
        RunId root, string agentId, CancellationToken ct)
    {
        SubagentRecord agent;
        int attempt;
        RunId runId;
        DateTimeOffset created;
        DateTimeOffset? admitted;
        DateTimeOffset? deadline;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.agent_id,s.root_run_id,s.internal_session_id,s.role,s.canceled,
                    a.attempt_number,a.run_id,a.created_at_utc,a.admitted_at_utc,a.deadline_at_utc
                FROM agent_subagents s JOIN agent_subagent_attempts a ON a.agent_id=s.agent_id
                WHERE s.root_run_id=$root AND s.agent_id=$agent ORDER BY a.attempt_number DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("$root", root.Value);
            command.Parameters.AddWithValue("$agent", agentId);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            agent = Read(reader);
            attempt = reader.GetInt32(5);
            runId = new RunId(reader.GetString(6));
            created = DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture);
            admitted = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
            deadline = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);
        }
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        return run is null ? null : new(agent, run, attempt, created, admitted, deadline);
    }
}
