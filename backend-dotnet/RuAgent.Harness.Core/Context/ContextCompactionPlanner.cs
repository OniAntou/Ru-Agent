using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Context;

public static partial class ContextCompactionPlanner
{
    private const int DefaultSummaryMaxChars = 4_000;

    public static ContextCompactionPlan? Plan(
        ModelContextSnapshot snapshot,
        ContextBudgetDecision decision,
        string compactionId,
        ContextCompactionReason reason,
        Func<ModelContextEntry, bool>? additionalProtection = null,
        int maxSummaryChars = DefaultSummaryMaxChars,
        long? estimatedTokensOverride = null,
        long retainRecentTokens = 0,
        long maximumCoveredTokens = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(compactionId);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxSummaryChars, 128);
        var beforeTokens = estimatedTokensOverride ?? ModelContextSizeEstimator.EstimateTokens(snapshot);
        if (beforeTokens <= decision.TargetTokens)
        {
            return null;
        }

        var entries = snapshot.Entries;
        var indexes = entries
            .Select((entry, index) => (entry.SourceId, index))
            .ToDictionary(pair => pair.SourceId, pair => pair.index, StringComparer.Ordinal);
        var completeBatches = FindCompleteBatches(entries, indexes);
        var completeBaseExchanges = FindCompleteBaseExchanges(entries);
        var protectedEntries = BuildProtectedEntries(
            snapshot,
            additionalProtection,
            completeBatches,
            completeBaseExchanges,
            indexes);
        var units = BuildCompactionUnits(entries, completeBatches, completeBaseExchanges, indexes);
        foreach (var unit in units)
        {
            unit.Measure();
        }

        var eligible = units.Where(unit => unit.Entries.All(entry => !protectedEntries.Contains(entry))).ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        long retained = 0;
        var eligibleCount = eligible.Length;
        foreach (var unit in eligible.Reverse())
        {
            if (retained >= retainRecentTokens || eligibleCount <= 1)
            {
                break;
            }

            protectedEntries.UnionWith(unit.Entries);
            eligibleCount--;
            retained += unit.EstimatedTokens;
        }

        var totalObjectBytes = units.Sum(unit => (long)unit.ObjectBytes);
        var remainingEntryCount = entries.Count;
        var remainingImageCount = entries.LongCount(entry => entry.ToolImage is not null);
        var usedRanges = 0;
        var coveredSet = new HashSet<ModelContextEntry>();
        var coveredSources = new HashSet<string>(StringComparer.Ordinal);
        long coveredTokens = 0;
        foreach (var unit in eligible)
        {
            if (unit.Entries.Any(protectedEntries.Contains))
            {
                continue;
            }

            var unitRangeCount = unit.Entries.Count(entry => !coveredSources.Contains(entry.SourceId));
            if (unitRangeCount > ContextCompactionIntent.MaxCoveredRanges - usedRanges)
            {
                continue;
            }

            // Auxiliary summaries must fit the same model. Keep whole groups;
            // one indivisible oversized group is left to exact summary preflight.
            if (coveredSet.Count > 0 && coveredTokens >= maximumCoveredTokens / 2
                && unit.EstimatedTokens > maximumCoveredTokens - coveredTokens) break;

            foreach (var entry in unit.Entries)
            {
                coveredSet.Add(entry);
                coveredSources.Add(entry.SourceId);
            }

            usedRanges += unitRangeCount;
            coveredTokens += unit.EstimatedTokens;
            totalObjectBytes -= unit.ObjectBytes;
            remainingEntryCount -= unit.Entries.Count;
            remainingImageCount -= unit.ImageCount;
            if (EstimateTokens(totalObjectBytes, remainingEntryCount, remainingImageCount)
                <= decision.TargetTokens)
            {
                break;
            }
        }

        if (coveredSet.Count == 0)
        {
            return null;
        }

        var covered = entries.Where(coveredSet.Contains).ToArray();
        return BuildPlan(snapshot, decision, compactionId, reason, covered, maxSummaryChars, beforeTokens);
    }

    private static ContextCompactionPlan? BuildPlan(
        ModelContextSnapshot snapshot,
        ContextBudgetDecision decision,
        string compactionId,
        ContextCompactionReason reason,
        IReadOnlyList<ModelContextEntry> covered,
        int maxSummaryChars,
        long beforeTokens)
    {
        var coveredSet = covered.ToHashSet();
        var ranges = BuildRanges(covered);
        if (ranges.Count is 0 or > ContextCompactionIntent.MaxCoveredRanges)
        {
            return null;
        }

        var coveredSnapshot = new ModelContextSnapshot(
            covered,
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
            ContextCompactionFrame.Wrap($"id={compactionId} ranges={rangePrefix} untrusted=true", summary),
            maxSummaryChars);
        var summarySequence = covered[0].Sequence;
        var summaryEntry = ModelContextEntry.Summary(
            compactionId,
            summarySequence,
            summaryText,
            ranges);
        var remaining = new List<ModelContextEntry>(snapshot.Entries.Count - covered.Count);
        var projected = new List<ModelContextEntry>(remaining.Capacity + 1);
        var insertedSummary = false;
        foreach (var entry in snapshot.Entries)
        {
            if (coveredSet.Contains(entry))
            {
                if (!insertedSummary)
                {
                    projected.Add(summaryEntry);
                    insertedSummary = true;
                }

                continue;
            }

            remaining.Add(entry);
            projected.Add(entry);
        }

        var projectedSnapshot = new ModelContextSnapshot(
            projected,
            ModelContextSnapshot.Canonicalize(projected));
        var afterTokens = ModelContextSizeEstimator.EstimateTokens(projectedSnapshot);
        if (afterTokens >= beforeTokens)
        {
            return null;
        }

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
            afterTokens,
            ComputeInputHash(coveredSnapshot, decision.Strategy, reason),
            snapshot.VerificationEvidence);
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

    private static long EstimateTokens(long objectBytes, int entryCount, long imageCount)
    {
        var bytes = objectBytes + Math.Max(0, entryCount - 1) + 2;
        return Math.Max(1, (bytes + 3) / 4
            + imageCount * RuAgent.Protocol.Harness.ContextTokenBreakdown.ImageTokenAllowance);
    }

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
