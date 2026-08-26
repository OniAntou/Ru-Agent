using RuAgent.Harness.Core.Reconciliation;

namespace RuAgent.Harness.Core.Context;

public interface IModelContextLimitProvider
{
    /// <summary>Provider-declared maximum prompt tokens for the run, or null when unlimited.</summary>
    long? MaxPromptTokens(RunId runId);
}

public sealed class StaticModelContextLimitProvider(long? limitTokens) : IModelContextLimitProvider
{
    public long? MaxPromptTokens(RunId runId) => limitTokens;
}

/// <summary>
/// Deterministic upper-bound estimate of the complete assembled model context. The
/// canonical UTF-8 projection is the same bytes the provider would receive; four
/// characters per token is a conservative stand-in for tokenizer counts.
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
