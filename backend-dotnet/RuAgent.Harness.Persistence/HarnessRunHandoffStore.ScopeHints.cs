using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    private const int ScopeHistoryStepLimit = 64;
    private const int ScopeReceiptLookupLimit = 128;

    private static async Task<IReadOnlyList<ModelContextEntry>> ReadHandoffScopeHistoryAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteModelContextSource source,
        RunId runId, IReadOnlyList<ModelContextEntry> recentEntries, HandoffReadBudget budget, CancellationToken ct)
    {
        long beforeSequence = recentEntries
            .Where(entry => entry.Kind is ModelContextEntryKind.AssistantStep or ModelContextEntryKind.ToolResult)
            .Select(entry => entry.Sequence).DefaultIfEmpty(long.MaxValue).Min();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT p.payload_id,p.kind,p.audience,p.encoding,p.byte_cap,
                   p.protected_content,p.protection_version,p.nonce,p.authentication_tag,p.context_scope,
                   p.context_source_id,p.context_sequence,p.context_role,p.context_entry_kind,
                   e.tool_call_id,e.provider_call_id,e.execution_id,e.safe_code,e.tool_outcome,e.side_effects_possible,
                   length(p.protected_content)
            FROM agent_payloads p LEFT JOIN agent_events e ON e.run_id=p.run_id
                AND substr(p.context_source_id,1,6)='event:'
                AND e.event_id=substr(p.context_source_id,7)
            WHERE p.run_id=$run AND p.context_scope=1 AND (p.audience & 2)=2
              AND p.kind={(int)HarnessPayloadKind.AssistantStep} AND p.context_sequence<$before
            ORDER BY p.context_sequence DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$before", beforeSequence);
        command.Parameters.AddWithValue("$limit", ScopeHistoryStepLimit);
        var steps = new List<ModelContextEntry>(ScopeHistoryStepLimit);
        var stepSequences = new List<long>(ScopeHistoryStepLimit);
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stepSequences.Add(reader.GetInt64(11));
                var entry = await ReadHandoffEntryAsync(reader, source, runId, budget, ct).ConfigureAwait(false);
                if (entry.AssistantStep is not null) steps.Add(entry);
            }
        }

        var history = new List<ModelContextEntry>(steps.Count + ScopeReceiptLookupLimit);
        history.AddRange(steps);
        var receiptLookups = 0;
        foreach (var step in steps)
        {
            var nextStepSequence = stepSequences.Where(sequence => sequence > step.Sequence)
                .DefaultIfEmpty(beforeSequence).Min();
            foreach (var call in step.AssistantStep!.ToolCalls)
            {
                if (receiptLookups == ScopeReceiptLookupLimit) break;
                if (!IsFileObservationCall(call)) continue;
                receiptLookups++;
                var receipt = await ReadSuccessfulHandoffReceiptAsync(connection, transaction,
                    runId, call.ProviderCallId, step.Sequence, nextStepSequence, ct).ConfigureAwait(false);
                if (receipt is null) continue;
                history.Add(receipt);
            }
            if (receiptLookups == ScopeReceiptLookupLimit) break;
        }
        return history.OrderBy(entry => entry.Sequence).ToArray();
    }

    private static bool IsFileObservationCall(ModelToolCall call) =>
        (call.ToolId is "read" or "read_file" or "write_file" or "edit_file" or "write" or "edit"
            or "files.read" or "files.write" or "files.edit")
        && call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object
        && call.Arguments.TryGetProperty("path", out var path)
        && path.ValueKind == System.Text.Json.JsonValueKind.String
        && path.GetString() is { Length: > 0 and <= 4096 };

    private static async Task<ModelContextEntry?> ReadSuccessfulHandoffReceiptAsync(
        SqliteConnection connection, SqliteTransaction transaction, RunId runId, string providerCallId,
        long afterSequence, long beforeSequence, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id,sequence,tool_call_id,execution_id,safe_code,tool_outcome,side_effects_possible
            FROM agent_events WHERE run_id=$run AND kind='tool.execution_completed'
              AND provider_call_id=$provider AND tool_outcome=$succeeded
              AND sequence>$after AND sequence<$before
            ORDER BY sequence DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$provider", providerCallId);
        command.Parameters.AddWithValue("$succeeded", (int)ToolExecutionOutcome.Succeeded);
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$before", beforeSequence);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)
            || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(6)) return null;
        var receipt = new ToolExecutionReceipt(new ExecutionId(reader.GetString(3)),
            new ToolCallId(reader.GetString(2)), ToolExecutionOutcome.Succeeded,
            reader.GetString(4), null, reader.GetBoolean(6), providerCallId: providerCallId);
        return ModelContextEntry.ToolResult("event:" + reader.GetString(0), reader.GetInt64(1), receipt, string.Empty);
    }
}
