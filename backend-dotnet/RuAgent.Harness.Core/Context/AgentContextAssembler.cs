namespace RuAgent.Harness.Core.Context;

public sealed class AgentContextAssembler
{
    private readonly IModelContextSource _source;

    public AgentContextAssembler(IModelContextSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<ModelContextSnapshot> AssembleAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        ModelContextSourceSnapshot source;
        using (Diagnostics.HarnessPerformance.Measure(Diagnostics.HarnessPhase.ContextRead))
            source = await _source.LoadAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("context_source_returned_null");
        cancellationToken.ThrowIfCancellationRequested();

        RejectDuplicateSequence(source.SessionBase);
        RejectDuplicateSequence(source.RunLocal);

        var session = source.SessionBase.OrderBy(entry => entry.Sequence).ToArray();
        var run = source.RunLocal.OrderBy(entry => entry.Sequence).ToArray();
        var combined = session.Concat(run).ToArray();
        if (combined.Select(entry => entry.SourceId).Distinct(StringComparer.Ordinal).Count()
            != combined.Length)
        {
            throw new InvalidOperationException("duplicate_context_source_id");
        }

        return new ModelContextSnapshot(
            combined,
            ModelContextSnapshot.Canonicalize(combined));
    }

    private static void RejectDuplicateSequence(IReadOnlyList<ModelContextEntry> entries)
    {
        if (entries.Select(entry => entry.Sequence).Distinct().Count() != entries.Count)
        {
            throw new InvalidOperationException("duplicate_context_sequence");
        }
    }
}
