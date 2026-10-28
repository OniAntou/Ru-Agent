using Microsoft.Data.Sqlite;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource
{
    private async Task ApplyCompactionsAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        RunId runId,
        List<ModelContextEntry> sessionBase, List<ModelContextEntry> runLocal,
        long fromVersion, long throughVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.compaction_id,
                   c.summary_payload_id,
                   c.covered_ranges_json,
                   p.kind,
                   p.audience,
                   p.encoding,
                   p.byte_cap,
                   p.protected_content,
                   p.protection_version,
                   p.nonce,
                   p.authentication_tag
            FROM agent_context_compactions AS c
            LEFT JOIN agent_payloads AS p
              ON p.run_id = c.run_id
             AND p.payload_id = c.summary_payload_id
            WHERE c.run_id = $run_id AND c.expected_run_version >= $from AND c.expected_run_version < $through
            ORDER BY c.expected_run_version, c.compaction_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$from", fromVersion);
        command.Parameters.AddWithValue("$through", throughVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(3)
                || reader.IsDBNull(4)
                || reader.IsDBNull(5)
                || reader.IsDBNull(6)
                || reader.IsDBNull(7)
                || reader.IsDBNull(8)
                || reader.IsDBNull(9)
                || reader.IsDBNull(10))
            {
                throw new HarnessStoreException("context_compaction_payload_missing");
            }

            var payloadId = new PayloadId(reader.GetString(1));
            var metadata = new HarnessPayloadIntent(
                payloadId,
                (HarnessPayloadKind)reader.GetInt32(3),
                (HarnessPayloadAudience)reader.GetInt32(4),
                (HarnessPayloadEncoding)reader.GetInt32(5),
                reader.GetInt32(6),
                ReadOnlyMemory<byte>.Empty);
            if (metadata.Kind != HarnessPayloadKind.ModelContextSummary
                || metadata.Audience != HarnessPayloadAudience.ModelContext
                || metadata.Encoding != HarnessPayloadEncoding.Utf8Text
                || !HarnessPayloadStore.CanReadForModel(metadata))
            {
                throw new HarnessStoreException("context_compaction_payload_invalid");
            }

            var encrypted = new EncryptedHarnessPayload(
                reader.GetInt32(8),
                HarnessSqliteMapper.ReadBoundedBlob(reader, 9, 12, "context_compaction_payload_invalid", 12),
                HarnessSqliteMapper.ReadBoundedBlob(reader, 7, metadata.ByteCap, "context_compaction_payload_invalid"),
                HarnessSqliteMapper.ReadBoundedBlob(reader, 10, 16, "context_compaction_payload_invalid", 16));
            var plaintext = await _payloadStore.DecryptAsync(
                    runId,
                    metadata,
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
            string summaryText;
            try
            {
                summaryText = StrictUtf8.GetString(plaintext.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new HarnessStoreException("context_compaction_payload_invalid", exception);
            }

            var ranges = ContextCompactionStoreCodec.DeserializeRanges(reader.GetString(2));
            var summary = ModelContextEntry.Summary(
                reader.GetString(0),
                ranges.Min(range => range.StartSequence),
                summaryText,
                ranges);
            ApplyCompaction(sessionBase, runLocal, summary);
        }
    }

    private static void ApplyCompaction(List<ModelContextEntry> sessionBase,
        List<ModelContextEntry> runLocal, ModelContextEntry summary)
    {
        var ranges = summary.SummaryRanges!.ToDictionary(range => range.SourceId, StringComparer.Ordinal);
        bool Covered(ModelContextEntry entry) => ranges.TryGetValue(entry.SourceId, out var range)
            && entry.Sequence >= range.StartSequence && entry.Sequence <= range.EndSequence;
        var first = sessionBase.Concat(runLocal).FirstOrDefault(Covered)
            ?? throw new HarnessStoreException("context_compaction_range_missing");
        var target = sessionBase.Contains(first) ? sessionBase : runLocal;
        var index = target.IndexOf(first);
        sessionBase.RemoveAll(entry => Covered(entry));
        runLocal.RemoveAll(entry => Covered(entry));
        target.Insert(index, ModelContextEntry.Summary(summary.SourceId, first.Sequence,
            summary.Content!, summary.SummaryRanges!));
    }

}
