using System.Security.Cryptography;
using System.Text;

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
    string InputHash);

public static class ContextCompactionPlanner
{
    private const int DefaultSummaryMaxChars = 4_000;

    public static ContextCompactionPlan? Plan(
        ModelContextSnapshot snapshot,
        ContextBudgetDecision decision,
        string compactionId,
        ContextCompactionReason reason,
        Func<ModelContextEntry, bool>? additionalProtection = null,
        int maxSummaryChars = DefaultSummaryMaxChars)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(compactionId);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxSummaryChars, 128);
        var beforeTokens = ModelContextSizeEstimator.EstimateTokens(snapshot);
        if (beforeTokens <= decision.TargetTokens)
        {
            return null;
        }

        var protectedEntries = BuildProtectedEntries(snapshot, additionalProtection);
        var eligible = snapshot.Entries
            .Where(entry => !protectedEntries.Contains(entry))
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var covered = new List<ModelContextEntry>();
        ContextCompactionPlan? best = null;
        foreach (var entry in eligible)
        {
            covered.Add(entry);
            var candidate = BuildPlan(
                snapshot,
                decision,
                compactionId,
                reason,
                covered,
                maxSummaryChars,
                beforeTokens);
            best = candidate;
            if (candidate.AfterTokens <= decision.TargetTokens)
            {
                break;
            }
        }

        return best;
    }

    private static ContextCompactionPlan BuildPlan(
        ModelContextSnapshot snapshot,
        ContextBudgetDecision decision,
        string compactionId,
        ContextCompactionReason reason,
        IReadOnlyList<ModelContextEntry> covered,
        int maxSummaryChars,
        long beforeTokens)
    {
        var coveredSet = covered.ToHashSet();
        var remaining = snapshot.Entries.Where(entry => !coveredSet.Contains(entry)).ToArray();
        var ranges = BuildRanges(covered);
        var coveredSnapshot = new ModelContextSnapshot(
            covered.ToArray(),
            ModelContextSnapshot.Canonicalize(covered));
        var summary = ContinuationSummaryBuilder.Build(
            coveredSnapshot,
            Math.Min(
                maxSummaryChars,
                (int)Math.Min(int.MaxValue, Math.Max(128L, decision.TargetTokens * 4)))).Text;
        var rangePrefix = string.Join(
            ",",
            ranges.Select(range => $"{range.SourceId}:{range.StartSequence}-{range.EndSequence}"));
        var summaryText = BoundSummary(
            $"[context_compaction id={compactionId} ranges={rangePrefix} untrusted=true]\n{summary}\n[/context_compaction]",
            maxSummaryChars);
        var summarySequence = covered.Min(entry => entry.Sequence);
        var summaryEntry = ModelContextEntry.Summary(
            compactionId,
            summarySequence,
            summaryText,
            ranges);
        var firstCoveredIndex = snapshot.Entries
            .Select((entry, index) => (entry, index))
            .Where(pair => coveredSet.Contains(pair.entry))
            .Min(pair => pair.index);
        var projected = remaining.ToList();
        var insertionIndex = snapshot.Entries
            .Take(firstCoveredIndex)
            .Count(entry => !coveredSet.Contains(entry));
        projected.Insert(insertionIndex, summaryEntry);
        var projectedSnapshot = new ModelContextSnapshot(
            projected,
            ModelContextSnapshot.Canonicalize(projected));
        return new ContextCompactionPlan(
            compactionId,
            decision.Strategy,
            reason,
            covered.ToArray(),
            remaining,
            projected,
            ranges,
            summaryText,
            beforeTokens,
            ModelContextSizeEstimator.EstimateTokens(projectedSnapshot),
            ComputeInputHash(coveredSnapshot, decision.Strategy, reason));
    }

    private static IReadOnlySet<ModelContextEntry> BuildProtectedEntries(
        ModelContextSnapshot snapshot,
        Func<ModelContextEntry, bool>? additionalProtection)
    {
        var protectedEntries = snapshot.Entries
            .Where(entry => entry.Role == ModelContextRole.System
                || entry.SourceId.StartsWith("base:", StringComparison.Ordinal))
            .ToHashSet();
        var latestUser = snapshot.Entries
            .Where(entry => entry.Role == ModelContextRole.User
                && !entry.SourceId.StartsWith("base:", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Sequence)
            .FirstOrDefault()
            ?? snapshot.Entries
                .Where(entry => entry.Role == ModelContextRole.User)
                .OrderByDescending(entry => entry.Sequence)
                .FirstOrDefault();
        if (latestUser is not null)
        {
            protectedEntries.Add(latestUser);
        }

        var unfinished = snapshot.Entries
            .Where(entry => entry.Kind == ModelContextEntryKind.AssistantStep
                && entry.AssistantStep?.FinishReason == AssistantFinishReason.ToolCalls)
            .Select(entry => entry.Sequence)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
        foreach (var entry in snapshot.Entries.Where(entry => entry.Sequence >= unfinished))
        {
            protectedEntries.Add(entry);
        }

        if (additionalProtection is not null)
        {
            foreach (var entry in snapshot.Entries.Where(additionalProtection))
            {
                protectedEntries.Add(entry);
            }
        }

        return protectedEntries;
    }

    private static IReadOnlyList<ContextEntryRange> BuildRanges(
        IReadOnlyList<ModelContextEntry> entries) =>
        entries
            .GroupBy(entry => entry.SourceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ContextEntryRange(
                group.Key,
                group.Min(entry => entry.Sequence),
                group.Max(entry => entry.Sequence)))
            .ToArray();

    private static string ComputeInputHash(
        ModelContextSnapshot covered,
        ContextCompactionStrategy strategy,
        ContextCompactionReason reason)
    {
        var prefix = Encoding.UTF8.GetBytes($"{strategy}|{reason}|");
        var bytes = new byte[prefix.Length + covered.CanonicalUtf8.Length];
        prefix.CopyTo(bytes, 0);
        covered.CanonicalUtf8.Span.CopyTo(bytes.AsSpan(prefix.Length));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string BoundSummary(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "...[truncated]";
}
