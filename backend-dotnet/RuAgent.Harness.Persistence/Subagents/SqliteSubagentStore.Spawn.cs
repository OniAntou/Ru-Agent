using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    public const int FamilyPageSize = 128;

    public async Task<IReadOnlyList<RunId>> ListFamiliesForSessionAsync(SessionId session, string? afterRootId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.run_id FROM agent_runs r WHERE r.session_id=$session AND r.run_id>$after
                AND (EXISTS(SELECT 1 FROM agent_subagent_families f WHERE f.root_run_id=r.run_id)
                    OR EXISTS(SELECT 1 FROM agent_internal_runs i WHERE i.root_run_id=r.run_id)
                    OR EXISTS(SELECT 1 FROM agent_subagent_workspace_allocations w WHERE w.root_run_id=r.run_id))
            ORDER BY r.run_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$session", session.Value);
        command.Parameters.AddWithValue("$after", afterRootId ?? "");
        command.Parameters.AddWithValue("$limit", FamilyPageSize);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<RunId>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(new(reader.GetString(0)));
        return result.AsReadOnly();
    }

    public async Task<bool> HasFamilyAsync(RunId root, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root)";
        command.Parameters.AddWithValue("$root", root.Value);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    public async Task<IReadOnlyList<RunId>> ListActiveFamiliesAsync(string? afterRootId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT s.root_run_id FROM agent_subagents s JOIN agent_subagent_attempts a ON a.agent_id=s.agent_id
            JOIN agent_runs r ON r.run_id=a.run_id WHERE r.terminal_at_utc IS NULL AND s.root_run_id>$after
            ORDER BY s.root_run_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", afterRootId ?? "");
        command.Parameters.AddWithValue("$limit", FamilyPageSize);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<RunId>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(new(reader.GetString(0)));
        return result.AsReadOnly();
    }

    public async Task<SubagentRecord?> FindSpawnAsync(RunId root, string invocationId, string agentId,
        SubagentRole role, string normalizedRequestHash, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id,root_run_id,internal_session_id,role,canceled,request_hash FROM agent_subagents
            WHERE root_run_id=$root AND invocation_id=$invocation
            """;
        command.Parameters.AddWithValue("$root", root.Value);
        command.Parameters.AddWithValue("$invocation", invocationId);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        if (reader.GetString(5) != CreationHash(agentId, role, normalizedRequestHash))
            throw new HarnessStoreException("idempotency_conflict");
        return Read(reader);
    }
}
