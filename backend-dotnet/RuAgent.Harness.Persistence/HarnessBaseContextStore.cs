using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    public async Task<BaseContextCaptureOutcome> CaptureBaseContextAsync(
        RunId runId,
        IReadOnlyList<HarnessBaseContextEntry> entries,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.ToArray();
        if (copied.Any(entry => entry is null)
            || copied.Select(entry => entry.SourceId).Distinct(StringComparer.Ordinal).Count()
                != copied.Length
            || copied.Select(entry => entry.Sequence).Distinct().Count() != copied.Length
            || copied.Select(entry => entry.PayloadId).Distinct().Count() != copied.Length)
        {
            throw new ArgumentException(
                "Base context entries must have unique identities.",
                nameof(entries));
        }

        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            SELECT base_context_captured_at_utc, terminal_at_utc
            FROM agent_runs
            WHERE run_id = $run_id
            """;
        state.Parameters.AddWithValue("$run_id", runId.Value);
        await using (var reader = await state.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new HarnessStoreException("context_run_not_found");
            }

            if (!reader.IsDBNull(0))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BaseContextCaptureOutcome.AlreadyCaptured;
            }

            if (!reader.IsDBNull(1))
            {
                throw new HarnessStoreException("context_run_terminal");
            }
        }

        foreach (var entry in copied.OrderBy(item => item.Sequence))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(entry.Content);
            var intent = new HarnessPayloadIntent(
                entry.PayloadId,
                HarnessPayloadKind.ModelContextBase,
                HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text,
                Math.Max(bytes.Length, 1),
                bytes);
            var protectedPayload = await ProtectAsync(runId, intent, cancellationToken)
                .ConfigureAwait(false);
            await InsertProtectedAsync(
                    connection,
                    transaction,
                    runId,
                    protectedPayload,
                    new ContextBinding(
                        Scope: 0,
                        entry.SourceId,
                        entry.Sequence,
                        entry.Role,
                        entry.Role == ModelContextRole.Assistant
                            ? ModelContextEntryKind.AssistantStep
                            : ModelContextEntryKind.Text),
                    capturedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_runs
            SET base_context_captured_at_utc = $captured_at,
                base_context_entry_count = $entry_count
            WHERE run_id = $run_id
              AND base_context_captured_at_utc IS NULL
              AND terminal_at_utc IS NULL
            """;
        update.Parameters.AddWithValue("$captured_at", HarnessSqliteMapper.Utc(capturedAt));
        update.Parameters.AddWithValue("$entry_count", copied.Length);
        update.Parameters.AddWithValue("$run_id", runId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("context_capture_conflict");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BaseContextCaptureOutcome.Captured;
    }
}
