using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence.Context;

namespace RuAgent.Harness.Persistence;

/// <summary>The newest earlier root run a new run may continue natively (ADR 0049).</summary>
public sealed record NativeContinuationCandidate(RunId RunId, string? TerminalErrorCode);

public sealed partial class HarnessPayloadStore
{
    /// <summary>
    /// The session's newest visible terminal root run before the target, when the target is
    /// an uncaptured root run with the same permission preset and profile. The caller still
    /// checks the pinned model and the carried size before choosing native continuation.
    /// </summary>
    public async Task<NativeContinuationCandidate?> ReadNativeContinuationCandidateAsync(RunId targetId,
        SessionId sessionId, IReadOnlyCollection<string> visibleSourceRunIds, CancellationToken ct)
    {
        var visible = ValidateVisibleSources(visibleSourceRunIds);
        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var target = await ReadHandoffTargetAsync(connection, transaction, targetId, sessionId, ct).ConfigureAwait(false);
        if (target is null || visible.Length == 0) return null;
        var newest = (await ReadHandoffSourcesAsync(connection, transaction, target, visible, limit: 1, ct)
            .ConfigureAwait(false)).FirstOrDefault();
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        // A different preset or profile declares a different tool set; carried calls to tools
        // the new run does not declare would be history the provider cannot pair.
        if (newest is null
            || newest.Preset != target.Preset
            || newest.PermissionProfileVersion != target.PermissionProfileVersion
            || !NativeRunContinuation.AllowsTerminalCode(newest.TerminalErrorCode))
            return null;
        return new NativeContinuationCandidate(newest.RunId, newest.TerminalErrorCode);
    }

    /// <summary>
    /// The source run a run continues natively, read from its captured marker; null for a run
    /// that reads earlier runs through the text handoff.
    /// </summary>
    public async Task<RunId?> ReadNativeContinuationSourceAsync(RunId runId, CancellationToken ct)
    {
        var options = RequireOptions();
        PayloadId payloadId;
        await using (var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_id FROM agent_payloads
                WHERE run_id=$run AND context_scope=0 AND context_source_id=$marker
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$run", runId.Value);
            command.Parameters.AddWithValue("$marker", NativeRunContinuation.MarkerSourceId);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string id) return null;
            payloadId = new PayloadId(id);
        }
        var content = await ReadForModelAsync(runId, payloadId, ct).ConfigureAwait(false);
        var marker = ModelContextEntry.Text(NativeRunContinuation.MarkerSourceId, 1, ModelContextRole.User,
            System.Text.Encoding.UTF8.GetString(content.Plaintext.Span));
        return NativeRunContinuation.DecodeMarker(marker)
            ?? throw new HarnessStoreException("native_continuation_marker_invalid");
    }

    /// <summary>The closed part of a source run's own projected history.</summary>
    public Task<NativeRunContinuation.Slice> ReadNativeContinuationSliceAsync(RunId sourceRunId, CancellationToken ct) =>
        SqliteModelContextSource.ReadContinuationSliceAsync(RequireOptions(), this, sourceRunId, ct);
}
