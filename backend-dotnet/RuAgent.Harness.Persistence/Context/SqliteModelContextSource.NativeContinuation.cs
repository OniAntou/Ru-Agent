using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource
{
    /// <summary>
    /// Reads the closed part of one terminal root run's own projected history (ADR 0049).
    /// The read neither caches nor follows that run's own continuation, so a run continues
    /// exactly one predecessor and the chain never grows.
    /// </summary>
    internal static async Task<NativeRunContinuation.Slice> ReadContinuationSliceAsync(
        HarnessSqliteOptions options, HarnessPayloadStore payloadStore, RunId sourceRunId, CancellationToken ct)
    {
        var reader = new SqliteModelContextSource(options, payloadStore, readsContinuationSource: true);
        var source = await reader.LoadAsync(sourceRunId, depth: 1, ct).ConfigureAwait(false);
        return NativeRunContinuation.Close(source.RunLocal);
    }

    /// <summary>
    /// Appends the source run's carried history after the captured base, with base sequences
    /// following it. The marker was written once at capture, so every load of this run
    /// rebuilds the same entries from the same immutable source.
    /// </summary>
    private async Task AppendNativeContinuationAsync(List<ModelContextEntry> sessionBase, CancellationToken ct)
    {
        var marker = sessionBase.FirstOrDefault(entry => entry.SourceId == NativeRunContinuation.MarkerSourceId);
        if (marker is null) return;
        var sourceRunId = NativeRunContinuation.DecodeMarker(marker)
            ?? throw new HarnessStoreException("native_continuation_marker_invalid");
        var slice = await ReadContinuationSliceAsync(_options, _payloadStore, sourceRunId, ct).ConfigureAwait(false);
        long sequence = sessionBase.Count == 0 ? 0 : sessionBase.Max(entry => entry.Sequence);
        foreach (var entry in slice.Entries)
        {
            sequence++;
            sessionBase.Add(entry.Kind switch
            {
                ModelContextEntryKind.Text => ModelContextEntry.Text(entry.SourceId, sequence, entry.Role, entry.Content!),
                ModelContextEntryKind.AssistantStep => ModelContextEntry.Assistant(entry.SourceId, sequence, entry.AssistantStep!),
                ModelContextEntryKind.ToolResult => ModelContextEntry.ToolResult(entry.SourceId, sequence, entry.ToolReceipt!,
                    entry.Content!),
                ModelContextEntryKind.Summary => ModelContextEntry.Summary(entry.SourceId, sequence, entry.Content!,
                    entry.SummaryRanges!),
                _ => throw new HarnessStoreException("native_continuation_entry_invalid"),
            });
        }
        if (slice.UnsettledNote is { } note)
            sessionBase.Add(ModelContextEntry.Text(NativeRunContinuation.NoteSourceId, ++sequence,
                ModelContextRole.User, note));
    }
}
