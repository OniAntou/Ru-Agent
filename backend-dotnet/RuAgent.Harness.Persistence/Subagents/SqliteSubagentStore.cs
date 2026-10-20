using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore(HarnessSqliteOptions options, HarnessPayloadStore payloads) : ISubagentStore
{
    private HarnessSqliteOptions Options => options;
    private HarnessPayloadStore Payloads => payloads;
    public async Task<SubagentRecord> CreateAsync(SubagentCreateCommand request, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(request.AgentId) || !SubagentContractValidator.IsValidId(request.InvocationId)
            || !Enum.IsDefined(request.Role)) throw new HarnessStoreException("subagent_arguments_invalid");
        HarnessSqliteWriteSet.ValidateNewRun(request.ChildRun);
        if (request.ChildRun.ConversationOutbox.Count != 0) throw new HarnessStoreException("subagent_outbox_denied");
        if (request.ChildRun.Run.State != HarnessRunState.Created) throw new HarnessStoreException("subagent_attempt_not_queued");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, request.RootRunId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_root_not_found");
        var hash = CreationHash(request.AgentId, request.Role, request.ChildRun.CreationIdentity.NormalizedRequestSha256);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash FROM agent_subagents WHERE root_run_id=$root AND invocation_id=$invocation";
        command.Parameters.AddWithValue("$root", root.RunId.Value);
        command.Parameters.AddWithValue("$invocation", request.InvocationId);
        var existing = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (existing is string previous)
        {
            if (previous != hash) throw new HarnessStoreException("idempotency_conflict");
            command.CommandText = "SELECT agent_id,root_run_id,internal_session_id,role,canceled FROM agent_subagents WHERE root_run_id=$root AND invocation_id=$invocation";
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            return Read(reader);
        }
        command.CommandText = "SELECT count(*) FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1";
        var canceled = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagents WHERE internal_session_id=$session) OR EXISTS(SELECT 1 FROM agent_internal_runs i JOIN agent_runs r ON r.run_id=i.run_id WHERE r.session_id=$session)";
        command.Parameters.AddWithValue("$session", root.SessionId.Value);
        var nested = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
        if (root.Status.State.IsTerminal() || canceled || nested || root.SessionId == request.ChildRun.Run.SessionId)
            throw new HarnessStoreException("subagent_root_inactive");
        if (root.ModelStepsInWindow > SubagentLimits.FamilyModelSteps || root.ToolCallsInWindow > SubagentLimits.FamilyToolCalls)
            throw new HarnessStoreException("subagent_family_budget_exhausted");
        if (root.Preset == AgentPermissionPreset.Plan && request.Role == SubagentRole.Coding
            || root.Preset != request.ChildRun.Run.Preset) throw new HarnessStoreException("subagent_role_denied");
        command.Parameters.AddWithValue("$agent", request.AgentId);
        command.CommandText = """
            SELECT count(*) FROM (
                SELECT agent_id FROM agent_subagents WHERE root_run_id=$root
                UNION SELECT agent_id FROM agent_subagent_workspace_allocations WHERE root_run_id=$root AND agent_id<>$agent)
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) >= SubagentLimits.MaxIdentities)
            throw new HarnessStoreException("subagent_identity_limit");
        if (await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, root.SessionId, ct).ConfigureAwait(false)
            || await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, request.ChildRun.Run.SessionId, ct).ConfigureAwait(false))
            throw new HarnessStoreException("session_deleted");
        if (!await HarnessSqliteQueries.WorkspaceExistsAsync(connection, transaction, request.ChildRun.Run.WorkspaceId, ct).ConfigureAwait(false))
            throw new HarnessStoreException("workspace_not_found");
        await InitializeInternalSessionAsync(connection, transaction, root, request.ChildRun, ct).ConfigureAwait(false);
        await HarnessSqliteWriteSet.InsertRunAsync(connection, transaction, request.ChildRun, ct).ConfigureAwait(false);
        await MarkInternalRunAsync(connection, transaction, request.ChildRun.Run.RunId, request.RootRunId, ct).ConfigureAwait(false);
        await payloads.InsertAsync(connection, transaction, request.ChildRun.Run.RunId, request.ChildRun.Payloads,
            request.ChildRun.Events, 1, request.ChildRun.Run.CreatedAt, ct).ConfigureAwait(false);
        await HarnessSqliteWriteSet.InsertEventsAsync(connection, transaction, request.ChildRun.Run.RunId, 1,
            request.ChildRun.Events, request.ChildRun.Run.CreatedAt, ct).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO agent_subagent_families(root_run_id) VALUES($root) ON CONFLICT DO NOTHING;
            INSERT INTO agent_subagent_budgets(root_run_id,window,model_steps,tool_calls)
            VALUES($root,$window,$steps,$tools) ON CONFLICT DO NOTHING;
            INSERT INTO agent_subagents(agent_id,root_run_id,internal_session_id,role,invocation_id,request_hash)
            VALUES($agent,$root,$child_session,$role,$invocation,$hash);
            INSERT INTO agent_subagent_attempts(agent_id,attempt_number,run_id,created_at_utc) VALUES($agent,1,$run,$created);
            """;
        command.Parameters.AddWithValue("$window", root.BudgetWindow);
        command.Parameters.AddWithValue("$steps", root.ModelStepsInWindow);
        command.Parameters.AddWithValue("$tools", root.ToolCallsInWindow);
        command.Parameters.AddWithValue("$child_session", request.ChildRun.Run.SessionId.Value);
        command.Parameters.AddWithValue("$role", (int)request.Role);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$run", request.ChildRun.Run.RunId.Value);
        command.Parameters.AddWithValue("$created", request.ChildRun.Run.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(request.AgentId, request.RootRunId, request.ChildRun.Run.SessionId, request.Role, false);
    }

    public async Task<IReadOnlyList<SubagentRecord>> ListAsync(RunId rootRunId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_id,root_run_id,internal_session_id,role,canceled FROM agent_subagents WHERE root_run_id=$root ORDER BY agent_id";
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        var result = new List<SubagentRecord>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(Read(reader));
        return result.AsReadOnly();
    }

    public async Task<bool> IsInternalSessionAsync(SessionId sessionId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagents WHERE internal_session_id=$session) OR EXISTS(SELECT 1 FROM agent_internal_runs i JOIN agent_runs r ON r.run_id=i.run_id WHERE r.session_id=$session)";
        command.Parameters.AddWithValue("$session", sessionId.Value);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    public async Task CancelFamilyAsync(RunId rootRunId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_subagent_families(root_run_id,canceled) VALUES($root,1)
            ON CONFLICT(root_run_id) DO UPDATE SET canceled=1;
            UPDATE agent_subagents SET canceled=1 WHERE root_run_id=$root;
            """;
        using var transaction = connection.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static SubagentRecord Read(SqliteDataReader reader) => new(reader.GetString(0), new RunId(reader.GetString(1)),
        new SessionId(reader.GetString(2)), (SubagentRole)reader.GetInt32(3), reader.GetInt32(4) != 0);

    private static string CreationHash(string agentId, SubagentRole role, string requestHash) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            agentId + ":" + role + ":" + requestHash))).ToLowerInvariant();
}
