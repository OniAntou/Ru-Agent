using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentSnapshotReader
{
    private async Task<IReadOnlyList<SubagentAttemptSnapshot>> ReadAttemptsAsync(SqliteConnection connection,
        SqliteTransaction transaction, SubagentRecord agent, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT run_id,attempt_number,created_at_utc,admitted_at_utc,deadline_at_utc FROM agent_subagent_attempts WHERE agent_id=$agent ORDER BY attempt_number LIMIT 4";
        command.Parameters.AddWithValue("$agent", agent.AgentId);
        var ids = new List<(RunId Id, int Number, DateTimeOffset Created, DateTimeOffset? Admitted, DateTimeOffset? Deadline)>();
        using (var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                if (ids.Count >= SubagentLimits.MaxAttempts || rows.GetInt32(1) != ids.Count + 1) Invalid();
                ids.Add((new(rows.GetString(0)), rows.GetInt32(1), HarnessSqliteMapper.ParseUtc(rows.GetString(2)),
                    rows.IsDBNull(3) ? null : HarnessSqliteMapper.ParseUtc(rows.GetString(3)),
                    rows.IsDBNull(4) ? null : HarnessSqliteMapper.ParseUtc(rows.GetString(4))));
            }
        }
        var result = new List<SubagentAttemptSnapshot>();
        foreach (var row in ids)
        {
            var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, row.Id, ct).ConfigureAwait(false)
                ?? throw new HarnessStoreException("subagent_attempt_missing");
            if (run.SessionId != agent.InternalSessionId || run.RunId == agent.RootRunId) Invalid();
            command.CommandText = "SELECT alias,model,protocol FROM agent_run_provider_selections WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", row.Id.Value);
            SubagentPinnedModel? model = null;
            using (var pin = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await pin.ReadAsync(ct).ConfigureAwait(false) && !pin.IsDBNull(0) && !pin.IsDBNull(1) && !pin.IsDBNull(2))
                    model = new(pin.GetString(0), pin.GetString(1), pin.GetString(2));
            }
            command.Parameters.RemoveAt("$run");
            result.Add(new(new(agent, run, row.Number, row.Created, row.Admitted, row.Deadline), model,
                await ReadApprovalAsync(connection, transaction, run, ct).ConfigureAwait(false)));
        }
        return result.AsReadOnly();
    }

    private async Task<string?> ReadTaskAsync(SqliteConnection connection, SqliteTransaction transaction,
        SubagentRecord agent, string invocation, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_id FROM agent_events WHERE run_id=$root AND kind='tool.proposed' AND tool_call_id=$invocation LIMIT 2";
        command.Parameters.AddWithValue("$root", agent.RootRunId.Value);
        command.Parameters.AddWithValue("$invocation", invocation);
        PayloadId id;
        using (var row = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(ct).ConfigureAwait(false) || row.IsDBNull(0)) return null;
            id = new(row.GetString(0));
            if (await row.ReadAsync(ct).ConfigureAwait(false)) Invalid();
        }
        var payload = await Payloads.ReadForClientAsync(connection, transaction, agent.RootRunId, id, ct).ConfigureAwait(false);
        if (payload.Kind != HarnessPayloadKind.ToolArguments || payload.Encoding != HarnessPayloadEncoding.Json) Invalid();
        var envelope = ToolArgumentsCodec.Deserialize(payload.Plaintext);
        if (envelope.ToolId != "agent_spawn" || envelope.ToolVersion != "1.0.0"
            || !SubagentContractValidator.Validate("agent_spawn", envelope.Arguments).IsValid
            || envelope.Arguments.GetProperty("role").GetString() != agent.Role.ToString().ToLowerInvariant()) Invalid();
        return envelope.Arguments.GetProperty("task").GetString();
    }
}
