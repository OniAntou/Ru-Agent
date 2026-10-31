namespace RuAgent.Harness.Core.Context;

public static partial class ContextCompactionPlanner
{
    private static HashSet<ModelContextEntry> BuildProtectedEntries(
        ModelContextSnapshot snapshot,
        Func<ModelContextEntry, bool>? additionalProtection,
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> completeBatches,
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> completeBaseExchanges,
        IReadOnlyDictionary<string, int> indexes)
    {
        var entries = snapshot.Entries;
        var completeBaseEntries = completeBaseExchanges
            .SelectMany(group => group.Value.Prepend(group.Key))
            .ToHashSet();
        var protectedEntries = entries
            .Where(entry => entry.Role == ModelContextRole.System
                || IsBaseInstruction(entry)
                || (IsBaseTurnDialogue(entry) && !completeBaseEntries.Contains(entry)))
            .ToHashSet();
        var scopeSnapshot = entries.LastOrDefault(Prompting.RuntimeContextSnapshot.IsSnapshot);
        if (scopeSnapshot?.Content is { } scopeContent
            && (scopeContent.Contains("\nobserved_path_b64: ", StringComparison.Ordinal)
                || scopeContent.Contains("\nobserved_dir_b64: ", StringComparison.Ordinal)))
        {
            protectedEntries.Add(scopeSnapshot);
        }

        var latestUser = entries.LastOrDefault(entry =>
                entry.Role == ModelContextRole.User
                && entry.Kind == ModelContextEntryKind.Text
                && !Prompting.RuntimeContextSnapshot.IsSnapshot(entry)
                && !entry.SourceId.StartsWith("base:", StringComparison.Ordinal))
            ?? entries.LastOrDefault(entry =>
                entry.Role == ModelContextRole.User
                && entry.Kind == ModelContextEntryKind.Text
                && !Prompting.RuntimeContextSnapshot.IsSnapshot(entry));
        if (latestUser is not null)
        {
            protectedEntries.Add(latestUser);
        }

        var unfinishedIndex = entries
            .Where(entry => entry.Kind == ModelContextEntryKind.AssistantStep
                && entry.AssistantStep?.FinishReason == AssistantFinishReason.ToolCalls
                && !completeBatches.ContainsKey(entry))
            .Select(entry => indexes[entry.SourceId])
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        for (var index = unfinishedIndex; index < entries.Count; index++)
        {
            protectedEntries.Add(entries[index]);
        }

        if (additionalProtection is not null)
        {
            foreach (var entry in entries.Where(additionalProtection))
            {
                protectedEntries.Add(entry);
            }
        }

        var completedGroupEntries = completeBatches
            .SelectMany(batch => batch.Value.Prepend(batch.Key))
            .ToHashSet();
        foreach (var result in entries.Where(entry =>
                     entry.Kind == ModelContextEntryKind.ToolResult
                     && !completedGroupEntries.Contains(entry)))
        {
            protectedEntries.Add(result);
        }

        ProtectCompleteGroups(completeBatches, protectedEntries);
        ProtectCompleteGroups(completeBaseExchanges, protectedEntries);
        return protectedEntries;
    }

    private static void ProtectCompleteGroups(
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> groups,
        HashSet<ModelContextEntry> protectedEntries)
    {
        foreach (var (first, members) in groups)
        {
            if (members.Prepend(first).Any(protectedEntries.Contains))
            {
                protectedEntries.Add(first);
                protectedEntries.UnionWith(members);
            }
        }
    }

    private static IReadOnlyList<CompactionUnit> BuildCompactionUnits(
        IReadOnlyList<ModelContextEntry> entries,
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> completeBatches,
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> completeBaseExchanges,
        IReadOnlyDictionary<string, int> indexes)
    {
        var groupByMember = new Dictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>>();
        AddGroups(completeBatches, groupByMember);
        foreach (var (first, members) in completeBaseExchanges)
        {
            var completeMembers = members.Prepend(first).ToArray();
            if (completeMembers.All(member => !groupByMember.ContainsKey(member)))
            {
                foreach (var member in completeMembers)
                {
                    groupByMember[member] = completeMembers;
                }
            }
        }

        var units = new List<CompactionUnit>();
        var consumed = new HashSet<ModelContextEntry>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (consumed.Contains(entry))
            {
                continue;
            }

            var members = groupByMember.TryGetValue(entry, out var group)
                ? group.OrderBy(member => indexes[member.SourceId]).ToArray()
                : [entry];
            consumed.UnionWith(members);
            units.Add(new CompactionUnit(members));
        }

        return units;
    }

    private static void AddGroups(
        IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> groups,
        IDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> groupByMember)
    {
        foreach (var (first, members) in groups)
        {
            var completeMembers = members.Prepend(first).ToArray();
            foreach (var member in completeMembers)
            {
                groupByMember[member] = completeMembers;
            }
        }
    }

    private static IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> FindCompleteBaseExchanges(
        IReadOnlyList<ModelContextEntry> entries)
    {
        var complete = new Dictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>>();
        for (var index = 0; index + 1 < entries.Count; index++)
        {
            var user = entries[index];
            var assistant = entries[index + 1];
            if (!IsBaseTurnUser(user)
                || assistant.Role != ModelContextRole.Assistant
                || assistant.Kind != ModelContextEntryKind.AssistantStep
                || !assistant.SourceId.StartsWith("base:turn:", StringComparison.Ordinal))
            {
                continue;
            }

            complete[user] = [assistant];
        }

        return complete;
    }

    private static bool IsBaseTurnDialogue(ModelContextEntry entry) =>
        entry.SourceId.StartsWith("base:turn:", StringComparison.Ordinal)
        && (IsBaseTurnUser(entry)
            || (entry.Role == ModelContextRole.Assistant && entry.Kind == ModelContextEntryKind.AssistantStep));

    private static bool IsBaseTurnUser(ModelContextEntry entry) =>
        entry.SourceId.StartsWith("base:turn:", StringComparison.Ordinal)
        && entry.Role == ModelContextRole.User
        && entry.Kind == ModelContextEntryKind.Text;

    private static bool IsBaseInstruction(ModelContextEntry entry) =>
        entry.SourceId.StartsWith("base:", StringComparison.Ordinal)
        && !entry.SourceId.StartsWith("base:turn:", StringComparison.Ordinal);

    private static IReadOnlyDictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>> FindCompleteBatches(
        IReadOnlyList<ModelContextEntry> entries,
        IReadOnlyDictionary<string, int> indexes)
    {
        var assistantSteps = entries
            .Where(entry => entry.Kind == ModelContextEntryKind.AssistantStep)
            .ToArray();
        var byProviderCall = entries
            .Where(entry => entry.Kind == ModelContextEntryKind.ToolResult)
            .GroupBy(entry => entry.ToolReceipt!.ProviderCallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var complete = new Dictionary<ModelContextEntry, IReadOnlyList<ModelContextEntry>>();
        for (var index = 0; index < assistantSteps.Length; index++)
        {
            var assistant = assistantSteps[index];
            if (assistant.AssistantStep?.FinishReason != AssistantFinishReason.ToolCalls)
            {
                continue;
            }

            var assistantIndex = indexes[assistant.SourceId];
            var nextAssistantIndex = index + 1 < assistantSteps.Length
                ? indexes[assistantSteps[index + 1].SourceId]
                : int.MaxValue;
            var receipts = MatchingReceipts(assistant, byProviderCall, assistantIndex, nextAssistantIndex, indexes);
            if (receipts is not null)
            {
                complete.Add(assistant, receipts);
            }
        }

        return complete;
    }

    private static ModelContextEntry[]? MatchingReceipts(
        ModelContextEntry assistant,
        IReadOnlyDictionary<string, ModelContextEntry[]> resultsByProviderCall,
        int assistantIndex,
        int nextAssistantIndex,
        IReadOnlyDictionary<string, int> indexes)
    {
        var calls = assistant.AssistantStep!.ToolCalls;
        var receipts = new List<ModelContextEntry>(calls.Count);
        foreach (var call in calls)
        {
            if (!resultsByProviderCall.TryGetValue(call.ProviderCallId, out var matches))
            {
                return null;
            }

            var afterAssistant = matches.Where(entry =>
                    indexes[entry.SourceId] > assistantIndex
                    && indexes[entry.SourceId] < nextAssistantIndex)
                .ToArray();
            if (afterAssistant.Length != 1)
            {
                return null;
            }

            receipts.Add(afterAssistant[0]);
        }

        return receipts.Distinct().Count() == calls.Count ? receipts.ToArray() : null;
    }

    private sealed class CompactionUnit(IReadOnlyList<ModelContextEntry> entries)
    {
        public IReadOnlyList<ModelContextEntry> Entries { get; } = entries;

        public int ObjectBytes { get; private set; }

        public long ImageCount { get; private set; }

        public long EstimatedTokens { get; private set; }

        public void Measure()
        {
            var canonical = ModelContextSnapshot.Canonicalize(Entries);
            ObjectBytes = canonical.Length - 2 - Math.Max(0, Entries.Count - 1);
            ImageCount = Entries.LongCount(entry => entry.ToolImage is not null);
            EstimatedTokens = EstimateTokens(ObjectBytes, Entries.Count, ImageCount);
        }
    }

}
