using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed record SubagentUserApply(RunId Root, string AgentId, long Version, string Key, string Status,
    string? Epoch, string? ApprovalId, string? Code, long Revision = 1)
{
    // Correlation IDs for a user action, never a provider/model invocation.
    public ToolProposal Proposal => new(Root, new("step_" + Key), new("call_" + Key), "user_apply_" + Key, 1,
        "agent_apply", "1.0.0", JsonSerializer.SerializeToElement(new { agent_id = AgentId, result_version = Version }));
    public ExecutionId ExecutionId => new("exec_" + Key);
}

public sealed class SqliteSubagentUserApplyStore(HarnessSqliteOptions options)
{
    private HarnessSqliteOptions Options { get; } = options;
    public async Task<SubagentUserApply> EnqueueAsync(RunId root, string agent, long version, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(agent) || version is < 1 or > 3)
            throw new HarnessStoreException("invalid_subagent_arguments");
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$root", root.Value);
        command.Parameters.AddWithValue("$agent", agent);
        command.Parameters.AddWithValue("$version", version);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_runs r JOIN agent_subagents s ON s.root_run_id=r.run_id
                JOIN agent_subagent_results v ON v.agent_id=s.agent_id
                JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
                JOIN agent_subagent_change_sets c ON c.run_id=v.run_id
                WHERE r.run_id=$root AND r.state IN ($running,$waiting_children,$waiting_user,$waiting_approval) AND s.agent_id=$agent AND s.role=$coding
                    AND v.result_version=$version AND o.state=$completed AND o.code IS NULL
                    AND NOT EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=r.run_id)
                    AND NOT EXISTS(SELECT 1 FROM agent_subagents WHERE internal_session_id=r.session_id)
                    AND NOT EXISTS(SELECT 1 FROM agent_session_tombstones WHERE session_id=r.session_id)
                    AND NOT EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=r.run_id AND canceled=1))
            """;
        command.Parameters.AddWithValue("$running", (int)HarnessRunState.Running);
        command.Parameters.AddWithValue("$waiting_children", (int)HarnessRunState.WaitingSubagents);
        command.Parameters.AddWithValue("$waiting_user", (int)HarnessRunState.WaitingUser);
        command.Parameters.AddWithValue("$waiting_approval", (int)HarnessRunState.WaitingApproval);
        command.Parameters.AddWithValue("$completed", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$coding", (int)SubagentRole.Coding);
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1)
            throw new HarnessStoreException("subagent_apply_root_or_result_inactive");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.Value + "\n" + agent + "\n" + version)))[..32];
        command.Parameters.AddWithValue("$key", key);
        command.CommandText = """
            INSERT INTO agent_subagent_user_applies(root_run_id,agent_id,result_version,action_key,status)
            VALUES($root,$agent,$version,$key,'queued') ON CONFLICT(root_run_id,agent_id,result_version) DO NOTHING
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = Select + " WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version";
        SubagentUserApply result;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("subagent_user_apply_missing");
            result = Read(reader);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<SubagentUserApply>> ListAsync(RunId root, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$root", root.Value);
        command.CommandText = Select + " WHERE root_run_id=$root ORDER BY agent_id,result_version LIMIT 25";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<SubagentUserApply> result = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(Read(reader));
        if (result.Count > 24) throw new HarnessStoreException("subagent_user_apply_invalid");
        return result;
    }

    public async Task<bool> UpdateAsync(SubagentUserApply expected, string status, string? epoch, string? approval,
        string? code, CancellationToken ct)
    {
        var allowed = expected.Status switch
        {
            "queued" => status is "executing" or "awaiting_approval" or "failed" or "cancelled",
            "awaiting_approval" => status is "queued" or "awaiting_approval" or "executing" or "failed" or "cancelled",
            "executing" => status is "completed" or "unknown",
            _ => false,
        };
        if (!allowed) throw new HarnessStoreException("subagent_user_apply_transition_invalid");
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_subagent_user_applies SET status=$status,epoch=$epoch,approval_id=$approval,code=$code,revision=revision+1
            WHERE root_run_id=$root AND action_key=$key AND revision=$revision AND status=$expected
            """;
        command.Parameters.AddWithValue("$root", expected.Root.Value);
        command.Parameters.AddWithValue("$key", expected.Key);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$expected", expected.Status);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$epoch", (object?)epoch ?? DBNull.Value);
        command.Parameters.AddWithValue("$approval", (object?)approval ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private const string Select = "SELECT root_run_id,agent_id,result_version,action_key,status,epoch,approval_id,code,revision FROM agent_subagent_user_applies";
    private static SubagentUserApply Read(SqliteDataReader r) => new(new(r.GetString(0)), r.GetString(1), r.GetInt64(2),
        r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8));
}
