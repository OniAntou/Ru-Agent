using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore
{
    private async Task SaveSubagentWaitAsync(SqliteConnection connection, SqliteTransaction transaction,
        AgentRun current, HarnessCommit commit, CancellationToken ct)
    {
        var wait = commit.SubagentWait!;
        if (current.State != HarnessRunState.Running || commit.NextRun.State != HarnessRunState.WaitingSubagents
            || commit.Events.Count != 1 || commit.Events[0].Kind != HarnessEventKind.RunStateChanged
            || commit.Events[0].ModelStepId != wait.ModelStepId)
            throw new HarnessStoreException("subagent_wait_projection_invalid");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", current.RunId.Value);
        command.Parameters.AddWithValue("$session", current.SessionId.Value);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_subagents WHERE internal_session_id=$session)
                OR EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1)
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0)
            throw new HarnessStoreException("subagent_root_inactive");
        foreach (var agentId in wait.AgentIds)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagents WHERE root_run_id=$root AND agent_id=$agent)";
            command.Parameters.AddWithValue("$agent", agentId);
            var owned = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) == 1;
            command.Parameters.RemoveAt("$agent");
            if (!owned) throw new HarnessStoreException("subagent_not_found");
        }
        var argumentsPayload = await ReadWaitArgumentsAsync(connection, transaction, current.RunId, wait, ct).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO agent_subagent_waits(root_run_id,invocation_id,arguments_payload_id,deadline_at_utc,delivered)
            VALUES($root,$invocation,$arguments,$deadline,0)
            ON CONFLICT(root_run_id) DO UPDATE SET invocation_id=$invocation,arguments_payload_id=$arguments,
                deadline_at_utc=$deadline,delivered=0 WHERE agent_subagent_waits.delivered=1
            """;
        command.Parameters.AddWithValue("$invocation", wait.ToolCallId.Value);
        command.Parameters.AddWithValue("$arguments", argumentsPayload.Value);
        command.Parameters.AddWithValue("$deadline", commit.NextRun.UpdatedAt.AddMilliseconds(wait.TimeoutMs).ToUniversalTime().ToString("O"));
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new HarnessStoreException("subagent_wait_pending");
    }

    private async Task<PayloadId> ReadWaitArgumentsAsync(SqliteConnection connection, SqliteTransaction transaction,
        RunId rootId, SubagentWaitIntent wait, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.payload_id FROM agent_events p JOIN agent_events r ON r.run_id=p.run_id AND r.tool_call_id=p.tool_call_id
            WHERE p.run_id=$root AND p.kind=$proposed AND p.model_step_id=$step AND p.tool_call_id=$call
                AND p.provider_call_id=$provider AND r.kind=$requested AND r.execution_id=$execution
                AND r.provider_call_id=$provider AND r.model_step_id=$step AND r.sequence>p.sequence
                AND NOT EXISTS(SELECT 1 FROM agent_events done WHERE done.run_id=$root AND done.execution_id=$execution AND done.kind=$completed)
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.Parameters.AddWithValue("$step", wait.ModelStepId.Value);
        command.Parameters.AddWithValue("$call", wait.ToolCallId.Value);
        command.Parameters.AddWithValue("$provider", wait.ProviderCallId);
        command.Parameters.AddWithValue("$execution", wait.ExecutionId.Value);
        command.Parameters.AddWithValue("$proposed", HarnessSqliteMapper.EventKind(HarnessEventKind.ToolProposed));
        command.Parameters.AddWithValue("$requested", HarnessSqliteMapper.EventKind(HarnessEventKind.ToolExecutionRequested));
        command.Parameters.AddWithValue("$completed", HarnessSqliteMapper.EventKind(HarnessEventKind.ToolExecutionCompleted));
        PayloadId payloadId;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
                throw new HarnessStoreException("subagent_wait_call_invalid");
            payloadId = new(reader.GetString(0));
            if (await reader.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("subagent_wait_call_invalid");
        }
        var payload = await _payloadStore.ReadForModelAsync(connection, transaction, rootId, payloadId, ct).ConfigureAwait(false);
        if (payload.Kind != HarnessPayloadKind.ToolArguments || payload.Encoding != HarnessPayloadEncoding.Json)
            throw new HarnessStoreException("subagent_wait_call_invalid");
        var arguments = ToolArgumentsCodec.Deserialize(payload.Plaintext);
        if (arguments.ToolId != "agent_wait" || arguments.ToolVersion != "1.0.0" || arguments.ProviderCallId != wait.ProviderCallId)
            throw new HarnessStoreException("subagent_wait_call_invalid");
        var original = new SubagentWaitIntent(wait.ModelStepId, wait.ToolCallId, wait.ExecutionId, wait.ProviderCallId, arguments.Arguments);
        if (original.TimeoutMs != wait.TimeoutMs || !original.AgentIds.SequenceEqual(wait.AgentIds, StringComparer.Ordinal))
            throw new HarnessStoreException("subagent_wait_call_invalid");
        return payloadId;
    }
}
