using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Persistence;

public sealed record ContextCompactionIntent
{
    public const int MaxCoveredRanges = 256;

    public ContextCompactionIntent(
        RunId runId,
        string compactionId,
        PayloadId summaryPayloadId,
        ContextCompactionStrategy strategy,
        ContextCompactionReason reason,
        IReadOnlyList<ContextEntryRange> coveredRanges,
        long beforeTokens,
        long afterTokens,
        string inputHash)
    {
        _ = runId.Value;
        _ = summaryPayloadId.Value;
        if (!IsToken(compactionId))
        {
            throw new ArgumentException("Compaction ID must be a bounded token.", nameof(compactionId));
        }

        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ArgumentNullException.ThrowIfNull(coveredRanges);
        if (coveredRanges.Count == 0
            || coveredRanges.Count > MaxCoveredRanges
            || coveredRanges.Any(range => range is null))
        {
            throw new ArgumentException("Compaction must contain bounded ranges.", nameof(coveredRanges));
        }

        if (beforeTokens < 0 || afterTokens < 0 || afterTokens > beforeTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(beforeTokens));
        }

        if (!IsHash(inputHash))
        {
            throw new ArgumentException("Input hash must be lowercase SHA-256 hex.", nameof(inputHash));
        }

        RunId = runId;
        CompactionId = compactionId;
        SummaryPayloadId = summaryPayloadId;
        Strategy = strategy;
        Reason = reason;
        CoveredRanges = coveredRanges.ToArray();
        BeforeTokens = beforeTokens;
        AfterTokens = afterTokens;
        InputHash = inputHash;
    }

    public RunId RunId { get; }

    public string CompactionId { get; }

    public PayloadId SummaryPayloadId { get; }

    public ContextCompactionStrategy Strategy { get; }

    public ContextCompactionReason Reason { get; }

    public IReadOnlyList<ContextEntryRange> CoveredRanges { get; }

    public long BeforeTokens { get; }

    public long AfterTokens { get; }

    public string InputHash { get; }

    private static bool IsToken(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '-'
                or '_'
                or '.');

    private static bool IsHash(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is (>= '0' and <= '9')
                or (>= 'a' and <= 'f'));
}
