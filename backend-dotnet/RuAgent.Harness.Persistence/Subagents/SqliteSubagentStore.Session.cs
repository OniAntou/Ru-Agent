using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    private static async Task InitializeInternalSessionAsync(SqliteConnection connection, SqliteTransaction transaction,
        AgentRun parent, NewAgentRun child, CancellationToken ct)
    {
        if (child.Run.Preset != parent.Preset || child.Run.PermissionProfileVersion != parent.PermissionProfileVersion
            || child.CreationIdentity.SessionPresetVersion != 1)
            throw new HarnessStoreException("subagent_role_denied");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", child.Run.SessionId.Value);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_session_settings WHERE session_id=$session)
                OR EXISTS(SELECT 1 FROM agent_runs WHERE session_id=$session)
                OR EXISTS(SELECT 1 FROM business_sessions WHERE session_id=$session)
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0)
            throw new HarnessStoreException("subagent_session_not_fresh");
        // This internal-only path follows a validated parent run. It neither changes
        // normal session selection nor copies grants, approvals or interactive consent.
        command.CommandText = """
            INSERT INTO agent_session_settings(session_id,preset,permission_profile_version,version,updated_at_utc)
            VALUES($session,$preset,$profile,1,$now)
            """;
        command.Parameters.AddWithValue("$preset", AgentPermissionPresetMapper.ToWireValue(parent.Preset));
        command.Parameters.AddWithValue("$profile", parent.PermissionProfileVersion);
        command.Parameters.AddWithValue("$now", child.Run.CreatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
