using Microsoft.Data.Sqlite;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessSessionArtifactStore
{
    private static async Task DeleteOwnedSubagentArtifactsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, DateTimeOffset deletedAt, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$now", HarnessSqliteMapper.Utc(deletedAt));
        // Freeze ownership before cascading relation rows. Temporary tables keep
        // cleanup set-based even when a conversation has many historical roots.
        command.CommandText = """
            CREATE TEMP TABLE subagent_deleting_sessions AS
                SELECT DISTINCT child.session_id AS internal_session_id FROM agent_internal_runs i
                JOIN agent_runs child ON child.run_id=i.run_id
                JOIN agent_runs root ON root.run_id=i.root_run_id WHERE root.session_id=$session;
            CREATE TEMP TABLE subagent_deleting_runs AS
                SELECT i.run_id FROM agent_internal_runs i
                JOIN agent_runs root ON root.run_id=i.root_run_id WHERE root.session_id=$session;
            INSERT INTO agent_session_tombstones(session_id,deleted_at_utc)
                SELECT internal_session_id,$now FROM subagent_deleting_sessions WHERE 1
                ON CONFLICT(session_id) DO NOTHING;
            DELETE FROM agent_subagents WHERE root_run_id IN(SELECT run_id FROM agent_runs WHERE session_id=$session);
            DELETE FROM agent_runs WHERE run_id IN(SELECT run_id FROM subagent_deleting_runs);
            DELETE FROM agent_session_settings WHERE session_id IN(SELECT internal_session_id FROM subagent_deleting_sessions);
            DELETE FROM agent_session_work_states WHERE session_id IN(SELECT internal_session_id FROM subagent_deleting_sessions);
            DELETE FROM agent_session_preset_snapshots WHERE session_id IN(SELECT internal_session_id FROM subagent_deleting_sessions);
            DROP TABLE subagent_deleting_runs;
            DROP TABLE subagent_deleting_sessions;
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
