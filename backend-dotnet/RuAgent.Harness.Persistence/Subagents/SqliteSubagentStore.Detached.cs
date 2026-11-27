using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

/// <summary>Detached children (ADR 0045): owned by the root's session once the root ends normally.</summary>
public sealed partial class SqliteSubagentStore
{
    // A child is visible to a root it belongs to, or, when detached, to any root of the
    // creating root's session. Internal sessions never own roots, so this never crosses
    // into another family's private history.
    internal const string VisibleToRoot = """
        (s.root_run_id=$root OR EXISTS(SELECT 1 FROM agent_subagent_detached d
            JOIN agent_runs owner ON owner.run_id=s.root_run_id
            JOIN agent_runs viewer ON viewer.run_id=$root
            WHERE d.agent_id=s.agent_id AND owner.session_id=viewer.session_id))
        """;

    /// <summary>
    /// The normal end of a root: stop the children it was waiting on and leave detached
    /// ones running. The family itself stays uncanceled, so an explicit cancel later still
    /// reaches the detached children through <see cref="CancelFamilyAsync"/>.
    /// </summary>
    public async Task CancelAttachedAsync(RunId rootRunId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_subagents SET canceled=1 WHERE root_run_id=$root
                AND agent_id NOT IN(SELECT agent_id FROM agent_subagent_detached)
            """;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunId>> ListAttemptRunIdsAsync(string agentId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM agent_subagent_attempts WHERE agent_id=$agent ORDER BY attempt_number";
        command.Parameters.AddWithValue("$agent", agentId);
        var runs = new List<RunId>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) runs.Add(new(reader.GetString(0)));
        return runs.AsReadOnly();
    }

    public async Task<bool> IsFamilyCanceledAsync(RunId rootRunId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1)";
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    /// <summary>The child <paramref name="agentId"/> as <paramref name="rootRunId"/> may address it, or null.</summary>
    public async Task<SubagentRecord?> FindVisibleAsync(RunId rootRunId, string agentId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RecordColumns} FROM agent_subagents s WHERE s.agent_id=$agent AND {VisibleToRoot}";
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Detached children created by earlier roots of <paramref name="rootRunId"/>'s session, newest root first.</summary>
    public async Task<IReadOnlyList<SubagentRecord>> ListInheritedDetachedAsync(RunId rootRunId, int limit, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {RecordColumns} FROM agent_subagents s
            JOIN agent_subagent_detached d ON d.agent_id=s.agent_id
            JOIN agent_runs owner ON owner.run_id=s.root_run_id
            JOIN agent_runs viewer ON viewer.run_id=$root
            WHERE owner.session_id=viewer.session_id AND s.root_run_id<>$root
            ORDER BY owner.created_at_utc DESC, s.agent_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 64));
        var result = new List<SubagentRecord>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(Read(reader));
        return result.AsReadOnly();
    }
}
