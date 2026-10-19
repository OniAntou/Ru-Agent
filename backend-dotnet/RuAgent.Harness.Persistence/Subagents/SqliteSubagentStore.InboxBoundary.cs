using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore
{
    private async Task<bool> IsInboxBoundaryAsync(SqliteConnection connection, SqliteTransaction transaction, RunId runId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", runId.Value);
        // Model completion precedes tool dispatch. Both incomplete phases belong to
        // recovery, including a crash before any native call has been proposed.
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_events started WHERE started.run_id=$run AND started.kind='model.step_started'
                AND NOT EXISTS(SELECT 1 FROM agent_events completed WHERE completed.run_id=$run
                    AND completed.model_step_id=started.model_step_id AND completed.kind='model.step_completed'))
                OR EXISTS(SELECT 1 FROM agent_events proposed WHERE proposed.run_id=$run AND proposed.kind='tool.proposed'
                AND NOT EXISTS(SELECT 1 FROM agent_events completed WHERE completed.run_id=$run
                    AND completed.model_step_id=proposed.model_step_id AND completed.tool_call_id=proposed.tool_call_id
                    AND completed.kind='tool.execution_completed'))
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0) return false;
        command.CommandText = """
            SELECT model_step_id,payload_id FROM agent_events WHERE run_id=$run
            AND kind='model.step_completed' AND payload_id IS NOT NULL ORDER BY sequence DESC LIMIT 1
            """;
        string step;
        PayloadId payloadId;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return true;
            if (reader.IsDBNull(0)) throw new HarnessStoreException("subagent_inbox_step_invalid");
            step = reader.GetString(0);
            payloadId = new(reader.GetString(1));
        }
        var payload = await Payloads.ReadForModelAsync(connection, transaction, runId, payloadId, ct).ConfigureAwait(false);
        if (payload.Kind != HarnessPayloadKind.AssistantStep) return true;
        var calls = AssistantStepJsonCodec.Deserialize(payload.Plaintext).ToolCalls;
        if (calls.Count == 0) return true;
        command.Parameters.AddWithValue("$step", step);
        command.CommandText = """
            SELECT provider_call_id FROM agent_events WHERE run_id=$run AND model_step_id=$step
            AND kind='tool.execution_completed' AND provider_call_id IS NOT NULL
            """;
        var completed = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) completed.Add(reader.GetString(0));
        return calls.All(call => completed.Contains(call.ProviderCallId));
    }
}
