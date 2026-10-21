using RuAgent.Harness.Core.Reconciliation;

namespace RuAgent.Harness.Core.Context;

public interface IModelContextLimitProvider
{
    /// <summary>Provider-declared maximum prompt tokens for the run, or null when unlimited.</summary>
    ValueTask<long?> MaxPromptTokensAsync(RunId runId, CancellationToken cancellationToken);
}

public sealed class StaticModelContextLimitProvider(long? limitTokens) : IModelContextLimitProvider
{
    public ValueTask<long?> MaxPromptTokensAsync(RunId runId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(limitTokens);
}

/// <summary>
/// Deterministic heuristic over canonical context UTF-8 bytes. This is not a
/// provider tokenizer or a proven upper bound. Prepared request measurement also
/// accounts for the provider envelope and may apply a guarded usage anchor.
/// </summary>
public static class ModelContextSizeEstimator
{
    public static long EstimateTokens(ModelContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var characters = snapshot.CanonicalUtf8.Length;
        return Math.Max(1, (characters + 3) / 4);
    }
}
