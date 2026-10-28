namespace RuAgent.Harness.Core.Context;

public enum ContextCompactionReason
{
    Automatic,
    Manual,
}

public sealed record ContextEntryRange
{
    public ContextEntryRange(string sourceId, long startSequence, long endSequence)
    {
        if (!IsSourceId(sourceId))
        {
            throw new ArgumentException("Context source ID must be a bounded opaque token.", nameof(sourceId));
        }

        if (startSequence <= 0 || endSequence < startSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(startSequence));
        }

        SourceId = sourceId;
        StartSequence = startSequence;
        EndSequence = endSequence;
    }

    public string SourceId { get; }

    public long StartSequence { get; }

    public long EndSequence { get; }

    private static bool IsSourceId(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '-'
                or '_'
                or '.'
                or ':');
}

public sealed record ContextCompactionPlan(
    string CompactionId,
    ContextCompactionStrategy Strategy,
    ContextCompactionReason Reason,
    IReadOnlyList<ModelContextEntry> CoveredEntries,
    IReadOnlyList<ModelContextEntry> RemainingEntries,
    IReadOnlyList<ModelContextEntry> ProjectedEntries,
    IReadOnlyList<ContextEntryRange> CoveredRanges,
    string SummaryText,
    long BeforeTokens,
    long AfterTokens,
    string InputHash,
    RunVerificationEvidence? VerificationEvidence = null);
