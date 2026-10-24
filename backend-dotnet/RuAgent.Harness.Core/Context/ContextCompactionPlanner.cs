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
    string InputHash,
    RunVerificationEvidence? VerificationEvidence = null);

public static class ContextCompactionPlanner
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
        long retainRecentTokens = 0)
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

        var completeBatches = FindCompleteBatches(snapshot.Entries);
        var protectedEntries = BuildProtectedEntries(snapshot, additionalProtection, completeBatches);
        var units = BuildCompactionUnits(snapshot, completeBatches);
        var eligibleUnitCount = units.Count(unit => unit.Entries.All(entry => !protectedEntries.Contains(entry)));
        long retained = 0;
        foreach (var unit in units.OrderByDescending(unit => unit.Sequence))
        {
            if (retained >= retainRecentTokens) break;
            if (unit.Entries.All(entry => !protectedEntries.Contains(entry)))
            {
                // Retention is a soft target; a single oversized old exchange must not prevent all progress.
                if (eligibleUnitCount <= 1) break;
                eligibleUnitCount--;
            }
            foreach (var entry in unit.Entries) protectedEntries.Add(entry);
            retained += ModelContextSizeEstimator.EstimateTokens(new ModelContextSnapshot(unit.Entries,
                ModelContextSnapshot.Canonicalize(unit.Entries)));
        }
        var eligible = units
            .Where(unit => unit.Entries.All(entry => !protectedEntries.Contains(entry)))
            .OrderBy(unit => unit.Sequence)
            .ThenBy(unit => unit.Entries[0].SourceId, StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var covered = new List<ModelContextEntry>();
        ContextCompactionPlan? best = null;
        foreach (var unit in eligible)
        {
            covered.AddRange(unit.Entries);
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
            ContextCompactionFrame.Wrap($"id={compactionId} ranges={rangePrefix} untrusted=true", summary),
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
            ComputeInputHash(coveredSnapshot, decision.Strategy, reason), snapshot.VerificationEvidence);
    }

    private static HashSet<ModelContextEntry> BuildProtectedEntries(
        ModelContextSnapshot snapshot,
        Func<ModelContextEntry, bool>? additionalProtection,
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> completeBatches)
    {
        var protectedEntries = snapshot.Entries
            .Where(entry => entry.Role == ModelContextRole.System
                || entry.SourceId.StartsWith("base:", StringComparison.Ordinal))
            .ToHashSet();
        var scopeSnapshot = snapshot.Entries.LastOrDefault(Prompting.RuntimeContextSnapshot.IsSnapshot);
        if (scopeSnapshot?.Content?.Contains("\nobserved_path_b64: ", StringComparison.Ordinal) == true)
            protectedEntries.Add(scopeSnapshot);
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
            .Where(entry => !completeBatches.ContainsKey(entry))
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

        var completedGroupEntries = completeBatches
            .SelectMany(batch => batch.Value.Prepend(batch.Key))
            .ToHashSet();
        foreach (var result in snapshot.Entries.Where(entry =>
                     entry.Kind == ModelContextEntryKind.ToolResult
                     && !completedGroupEntries.Contains(entry)))
        {
            protectedEntries.Add(result);
        }

        foreach (var (assistant, receipts) in completeBatches)
        {
            if (new[] { assistant }.Concat(receipts).Any(protectedEntries.Contains))
            {
                protectedEntries.Add(assistant);
                protectedEntries.UnionWith(receipts);
            }
        }
        return protectedEntries;
    }

    private static IReadOnlyList<CompactionUnit> BuildCompactionUnits(
        ModelContextSnapshot snapshot,
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> completeBatches)
    {
        var grouped = new HashSet<ModelContextEntry>();
        var units = new List<CompactionUnit>();
        foreach (var (assistant, receipts) in completeBatches)
        {
            var members = new[] { assistant }.Concat(receipts).OrderBy(entry => entry.Sequence).ToArray();
            foreach (var member in members) grouped.Add(member);
            units.Add(new CompactionUnit(members[0].Sequence, members));
        }

        foreach (var entry in snapshot.Entries.Where(entry => !grouped.Contains(entry)))
        {
            units.Add(new CompactionUnit(entry.Sequence, [entry]));
        }

        return units;
    }

    private static IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> FindCompleteBatches(
        IReadOnlyList<ModelContextEntry> entries)
    {
        var assistantSteps = entries.Where(entry => entry.Kind == ModelContextEntryKind.AssistantStep)
            .OrderBy(entry => entry.Sequence)
            .ToArray();
        var assistants = assistantSteps.Where(entry => entry.AssistantStep?.FinishReason == AssistantFinishReason.ToolCalls)
            .ToArray();
        var byProviderCall = entries.Where(entry => entry.Kind == ModelContextEntryKind.ToolResult)
            .GroupBy(entry => entry.ToolReceipt!.ProviderCallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var complete = new Dictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>>();
        for (var index = 0; index < assistants.Length; index++)
        {
            var assistant = assistants[index];
            var nextAssistantSequence = assistantSteps.FirstOrDefault(entry => entry.Sequence > assistant.Sequence)?.Sequence
                ?? long.MaxValue;
            var receipts = MatchingReceipts(assistant, byProviderCall, nextAssistantSequence);
            if (receipts is not null) complete.Add(assistant, receipts);
        }
        return complete;
    }

    private static ModelContextEntry[]? MatchingReceipts(
        ModelContextEntry assistant,
        IReadOnlyDictionary<string, ModelContextEntry[]> resultsByProviderCall,
        long nextAssistantSequence)
    {
        var calls = assistant.AssistantStep!.ToolCalls;
        var receipts = new List<ModelContextEntry>(calls.Count);
        foreach (var call in calls)
        {
            if (!resultsByProviderCall.TryGetValue(call.ProviderCallId, out var matches)) return null;
            var afterAssistant = matches.Where(entry => entry.Sequence > assistant.Sequence
                && entry.Sequence < nextAssistantSequence).ToArray();
            if (afterAssistant.Length != 1) return null;
            receipts.Add(afterAssistant[0]);
        }

        return receipts.Distinct().Count() == calls.Count ? receipts.ToArray() : null;
    }

    private sealed record CompactionUnit(long Sequence, IReadOnlyList<ModelContextEntry> Entries);

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
