using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore
{
    private async Task<ToolArgumentsEnvelope?> ReadSubagentReconciliationArgumentsAsync(SqliteConnection connection,
        SqliteTransaction transaction, RunId root, HarnessEventIntent resolution, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", root.Value);
        command.CommandText = """
            SELECT model_step_id,tool_call_id,execution_id,safe_code,sequence FROM agent_events
            WHERE run_id=$root AND kind='reconciliation.required' ORDER BY sequence DESC LIMIT 1
            """;
        long requiredSequence;
        bool subagentRequired;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            if (reader.IsDBNull(0) && reader.IsDBNull(1) && reader.IsDBNull(2)) return null;
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2)
                || reader.GetString(0) != resolution.ModelStepId?.Value || reader.GetString(1) != resolution.ToolCallId?.Value
                || reader.GetString(2) != resolution.ExecutionId?.Value)
                throw new HarnessStoreException("subagent_reconciliation_target_invalid");
            subagentRequired = !reader.IsDBNull(3) && reader.GetString(3).StartsWith("subagent_apply_", StringComparison.Ordinal);
            requiredSequence = reader.GetInt64(4);
        }
        command.Parameters.AddWithValue("$step", resolution.ModelStepId!.Value.Value);
        command.Parameters.AddWithValue("$call", resolution.ToolCallId!.Value.Value);
        command.Parameters.AddWithValue("$execution", resolution.ExecutionId!.Value.Value);
        // Explicit user actions have their own immutable provenance, never fabricated
        // provider tool.proposed/model.step_started events.
        command.CommandText = """
            SELECT agent_id,result_version,action_key FROM agent_subagent_user_applies
            WHERE root_run_id=$root AND 'step_'||action_key=$step AND 'call_'||action_key=$call
                AND 'exec_'||action_key=$execution AND status IN ('executing','unknown')
            """;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return ToolArgumentsCodec.Deserialize(ToolArgumentsCodec.Serialize("user_apply_" + reader.GetString(2),
                    "agent_apply", "1.0.0", System.Text.Json.JsonSerializer.SerializeToElement(
                        new { agent_id = reader.GetString(0), result_version = reader.GetInt64(1) })));
            }
        }
        command.CommandText = """
            SELECT p.payload_id,p.provider_call_id FROM agent_events p JOIN agent_events r
                ON r.run_id=p.run_id AND r.tool_call_id=p.tool_call_id AND r.model_step_id=p.model_step_id
                    AND r.provider_call_id=p.provider_call_id
            WHERE p.run_id=$root AND p.kind='tool.proposed' AND p.model_step_id=$step AND p.tool_call_id=$call
                AND r.kind='tool.execution_requested' AND r.execution_id=$execution
                AND r.sequence>p.sequence AND r.sequence<$required
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$required", requiredSequence);
        PayloadId payloadId;
        string provider;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                if (subagentRequired) throw new HarnessStoreException("subagent_reconciliation_call_invalid");
                return null;
            }
            payloadId = new(reader.GetString(0));
            provider = reader.GetString(1);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new HarnessStoreException("subagent_reconciliation_call_invalid");
        }
        var payload = await _payloadStore.ReadForModelAsync(connection, transaction, root, payloadId, ct).ConfigureAwait(false);
        if (payload.Kind != HarnessPayloadKind.ToolArguments || payload.Encoding != HarnessPayloadEncoding.Json)
            throw new HarnessStoreException("subagent_reconciliation_call_invalid");
        var arguments = ToolArgumentsCodec.Deserialize(payload.Plaintext);
        if (arguments.ProviderCallId != provider) throw new HarnessStoreException("subagent_reconciliation_call_invalid");
        return arguments;
    }
}
