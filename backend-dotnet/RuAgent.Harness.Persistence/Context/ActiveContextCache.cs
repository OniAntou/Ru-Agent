using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Context;

/// <summary>Bounded active projection, not an additional owner of original history.</summary>
internal sealed class ActiveContextCache
{
    private const int MaximumRuns = 8;
    private const long MaximumBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal sealed record Entry(string KeyStamp, long Revision, long Version, long Sequence, int BaseCount,
        ModelContextEntry[] Base, ModelContextEntry[] Local, IReadOnlyList<string> Paths,
        RunVerificationEvidence Verification, ModelContextEntry? LastAssistant,
        IReadOnlyList<ActiveSkillSnapshots.Snapshot> ActiveSkills, IReadOnlyList<string> SkillOmissions,
        int AdditionalSkillOmissionCount,
        long Weight, DateTimeOffset Accessed);

    internal Entry? Get(string runId, string key, long revision, long version, long sequence, int baseCount)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(runId, out var entry)) return null;
            if (entry.KeyStamp != key || entry.Revision != revision || version < entry.Version
                || sequence < entry.Sequence || entry.BaseCount != baseCount
                || DateTimeOffset.UtcNow - entry.Accessed > Lifetime)
            {
                _entries.Remove(runId);
                return null;
            }
            return _entries[runId] = entry with { Accessed = DateTimeOffset.UtcNow };
        }
    }

    internal void Store(string runId, Entry entry)
    {
        lock (_gate)
        {
            _entries.Remove(runId);
            if (entry.Weight > MaximumBytes / 2) return;
            while (_entries.Count >= MaximumRuns || _entries.Values.Sum(e => e.Weight) + entry.Weight > MaximumBytes)
                _entries.Remove(_entries.MinBy(e => e.Value.Accessed).Key);
            _entries[runId] = entry;
        }
    }

    internal void Clear() { lock (_gate) _entries.Clear(); }

    internal static long Weight(IEnumerable<ModelContextEntry> entries) => entries.Sum(entry =>
        512L + (entry.Content?.Length ?? 0) * 2L + (entry.AssistantStep?.Text?.Length ?? 0) * 2L
        + (entry.AssistantStep?.ToolCalls.Sum(c => (long)c.Arguments.GetRawText().Length * 2 + 256) ?? 0)
        // Native public message envelopes may contain large annotations beside
        // tiny visible text. Reserve their full bound without warm JSON traversal.
        + (entry.AssistantStep?.ResponsesMessages is null ? 0
            : 4L * Core.Prompting.ResponsesAssistantMessages.MaximumBytes)
        + (entry.SummaryRanges?.Count ?? 0) * 256L);

    internal static long SkillWeight(IEnumerable<ActiveSkillSnapshots.Snapshot> snapshots) =>
        snapshots.Sum(snapshot => 512L + snapshot.Body.Length * 2L + snapshot.Name.Length * 2L
            + snapshot.Description.Length * 2L + snapshot.SourcePath.Length * 2L
            + snapshot.ResourceBase.Length * 2L + snapshot.Revision.Length * 2L
            + snapshot.Arguments.Length * 2L);

    internal static long SkillOmissionWeight(IEnumerable<string> omissions) =>
        omissions.Sum(item => 64L + item.Length * 2L);
}
