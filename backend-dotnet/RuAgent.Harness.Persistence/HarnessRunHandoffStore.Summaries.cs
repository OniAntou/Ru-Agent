using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private async Task<(IReadOnlyList<ModelContextEntry> Entries, bool Omitted)> ReadHandoffSummariesAsync(
        SqliteConnection connection, SqliteTransaction transaction, RunId runId,
        HandoffReadBudget budget, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH recent_compactions AS (
                SELECT compaction_id,run_id,summary_payload_id,expected_run_version,covered_ranges_json
                FROM agent_context_compactions WHERE run_id=$run
                ORDER BY expected_run_version DESC,compaction_id DESC LIMIT $limit
            )
            SELECT c.compaction_id,c.covered_ranges_json,p.payload_id,p.kind,p.audience,p.encoding,
                   length(p.protected_content)
            FROM recent_compactions c
            JOIN agent_payloads p ON p.run_id=c.run_id AND p.payload_id=c.summary_payload_id
            WHERE NOT EXISTS(
                SELECT 1
                FROM recent_compactions newer, json_each(newer.covered_ranges_json) covered
                WHERE newer.run_id=c.run_id
                  AND (newer.expected_run_version>c.expected_run_version
                       OR (newer.expected_run_version=c.expected_run_version AND newer.compaction_id>c.compaction_id))
                  AND json_extract(covered.value,'$.source_id')=c.compaction_id
              )
            ORDER BY c.expected_run_version DESC,c.compaction_id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$limit", SessionRunHandoff.MaximumSummariesPerRun);
        var entries = new List<ModelContextEntry>(SessionRunHandoff.MaximumSummariesPerRun);
        var summaries = new List<(string Id, string RangesJson, PayloadId PayloadId, long Sequence,
            HarnessPayloadKind Kind, HarnessPayloadAudience Audience, HarnessPayloadEncoding Encoding, int Bytes)>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                summaries.Add((reader.GetString(0), reader.GetString(1), new PayloadId(reader.GetString(2)),
                    ContextCompactionStoreCodec.DeserializeRanges(reader.GetString(1))[0].StartSequence,
                    (HarnessPayloadKind)reader.GetInt32(3), (HarnessPayloadAudience)reader.GetInt32(4),
                    (HarnessPayloadEncoding)reader.GetInt32(5), reader.GetInt32(6)));
        foreach (var summary in summaries)
        {
            if (summary.Kind != HarnessPayloadKind.ModelContextSummary
                || summary.Audience != HarnessPayloadAudience.ModelContext
                || summary.Encoding != HarnessPayloadEncoding.Utf8Text)
                throw new HarnessStoreException("context_compaction_payload_invalid");
            if (summary.Bytes > HandoffPayloadByteLimit || summary.Bytes > budget.RemainingBytes)
            {
                budget.Omitted = true;
                budget.OmittedPayloadCount++;
                entries.Add(ModelContextEntry.Text(summary.Id, summary.Sequence, ModelContextRole.User,
                    $"[Historical summary omitted by handoff read limit; bytes={summary.Bytes}; compaction remains applicable.]"));
                continue;
            }
            budget.RemainingBytes -= summary.Bytes;
            var payload = await ReadForModelAsync(connection, transaction, runId, summary.PayloadId, ct).ConfigureAwait(false);
            string text;
            try { text = StrictUtf8.GetString(payload.Plaintext.Span); }
            catch (DecoderFallbackException error)
            {
                throw new HarnessStoreException("context_compaction_payload_invalid", error);
            }
            entries.Add(ModelContextEntry.Summary(summary.Id, summary.Sequence, text,
                ContextCompactionStoreCodec.DeserializeRanges(summary.RangesJson)));
        }
        entries.Reverse();
        using var moreCommand = connection.CreateCommand();
        moreCommand.Transaction = transaction;
        moreCommand.CommandText = """
            SELECT 1 FROM agent_context_compactions WHERE run_id=$run
            ORDER BY expected_run_version DESC,compaction_id DESC LIMIT 1 OFFSET $limit
            """;
        moreCommand.Parameters.AddWithValue("$run", runId.Value);
        moreCommand.Parameters.AddWithValue("$limit", SessionRunHandoff.MaximumSummariesPerRun);
        var omitted = await moreCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        return (entries, omitted);
    }
}
