using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore
{
    private static readonly UTF8Encoding SubagentResultUtf8 = new(false, true);
    private sealed record SubagentWaitPreview(SubagentResult Result, byte[] Prefix, int TotalBytes);

    private async Task<IReadOnlyList<SubagentWaitPreview>> LoadSubagentWaitResultsAsync(SqliteConnection connection,
        SqliteTransaction transaction, RunId rootId, SubagentWaitIntent wait, CancellationToken ct)
    {
        var previews = new List<SubagentWaitPreview>();
        foreach (var agentId in wait.AgentIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT v.result_version,v.run_id,o.state,o.code,v.payload_id,a.attempt_number,r.state
                FROM agent_subagent_results v JOIN agent_subagents s ON s.agent_id=v.agent_id JOIN agent_runs r ON r.run_id=v.run_id
                LEFT JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
                LEFT JOIN agent_subagent_attempts a ON a.agent_id=v.agent_id AND a.run_id=v.run_id
                WHERE s.root_run_id=$root AND s.agent_id=$agent AND v.delivered=0 ORDER BY v.result_version LIMIT 1
                """;
            command.Parameters.AddWithValue("$root", rootId.Value);
            command.Parameters.AddWithValue("$agent", agentId);
            SubagentResult result;
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) continue;
                if (reader.IsDBNull(2)) throw new HarnessStoreException("subagent_result_outcome_missing");
                SubagentResultRow.Validate(reader.GetInt64(0), reader.IsDBNull(5) ? 0 : reader.GetInt64(5), (HarnessRunState)reader.GetInt32(2), (HarnessRunState)reader.GetInt32(6));
                result = new(agentId, reader.GetInt64(0), new(reader.GetString(1)), (HarnessRunState)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), new(reader.GetString(4)), false);
            }
            if (!result.State.IsTerminal()) throw new HarnessStoreException("subagent_result_not_terminal");
            var summary = await _payloadStore.ReadForModelAsync(connection, transaction, result.RunId, result.PayloadId, ct).ConfigureAwait(false);
            if (summary.Kind != HarnessPayloadKind.AssistantText || summary.Encoding != HarnessPayloadEncoding.Utf8Text)
                throw new HarnessStoreException("subagent_result_payload_invalid");
            previews.Add(new(result, summary.Plaintext.Span[..Math.Min(summary.Plaintext.Length, SubagentLimits.MaxInlineResultBytes)].ToArray(),
                summary.Plaintext.Length));
        }
        return previews.AsReadOnly();
    }

    private static byte[] SubagentWaitOutput(IReadOnlyList<SubagentWaitPreview> previews, bool timedOut)
    {
        var summaryCap = SubagentLimits.MaxInlineResultBytes;
        while (true)
        {
            var output = JsonSerializer.SerializeToUtf8Bytes(new
            {
                source = "untrusted_subagent_results",
                timed_out = timedOut,
                results = previews.Select(preview =>
                {
                    var length = Math.Min(preview.Prefix.Length, summaryCap);
                    // Trim a possibly incomplete final UTF-8 scalar using strict decoding.
                    string summary;
                    while (true)
                    {
                        try { summary = SubagentResultUtf8.GetString(preview.Prefix, 0, length); break; }
                        catch (DecoderFallbackException) when (length > 0) { length--; }
                    }
                    return new { agent_id = preview.Result.AgentId, result_version = preview.Result.Version,
                        run_id = preview.Result.RunId.Value, state = preview.Result.State.ToString().ToLowerInvariant(),
                        code = preview.Result.Code, summary, summary_truncated = length < preview.TotalBytes,
                        summary_bytes = preview.TotalBytes, summary_payload_id = preview.Result.PayloadId.Value };
                }).ToArray(),
            });
            if (output.Length <= SubagentLimits.MaxInlineResultBytes) return output;
            if (summaryCap == 0) throw new HarnessStoreException("subagent_result_too_large");
            summaryCap /= 2;
        }
    }
}
