using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// Attributes worktree deltas observed between the run-start and run-end snapshots to a
/// specific run. Only paths backed by verified before/after evidence are attributed;
/// pre-existing dirty paths that never moved stay excluded, and every unexplained change
/// (terminal-created, concurrent, or reverted) is flagged ambiguous instead of being
/// attributed silently.
/// </summary>
public sealed class GitChangeAttributor
{
    /// <summary>Hash sentinel for a path absent from a snapshot's observed set.</summary>
    public const string AbsentHash = "absent";

    public RunChangeAttributionResult Attribute(
        RunGitWorktreeSnapshot start,
        RunGitWorktreeSnapshot end,
        IReadOnlyCollection<RunGitMutationEvidence> verifiedDeltas)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(verifiedDeltas);

        var dirtyAtStart = start.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var attributed = new List<string>();
        var excluded = new List<string>();
        var ambiguous = new List<string>();

        foreach (var path in EnumerateUnionPaths(start.ObservedFileHashes, end.ObservedFileHashes))
        {
            var before = start.ObservedFileHashes.TryGetValue(path, out var b) ? b : AbsentHash;
            var after = end.ObservedFileHashes.TryGetValue(path, out var a) ? a : AbsentHash;

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                if (dirtyAtStart.Contains(path))
                {
                    excluded.Add(path);
                }

                continue;
            }

            var evidence = FindVerifiedDelta(verifiedDeltas, path);
            if (evidence is not null
                && string.Equals(evidence.BeforeHash, before, StringComparison.Ordinal)
                && string.Equals(evidence.AfterHash, after, StringComparison.Ordinal))
            {
                attributed.Add(path);
            }
            else
            {
                ambiguous.Add(path);
            }
        }

        return new RunChangeAttributionResult(attributed, excluded, ambiguous);
    }

    private static IEnumerable<string> EnumerateUnionPaths(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in first.Keys)
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (var path in second.Keys)
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static RunGitMutationEvidence? FindVerifiedDelta(
        IReadOnlyCollection<RunGitMutationEvidence> verifiedDeltas,
        string path)
    {
        RunGitMutationEvidence? match = null;
        foreach (var candidate in verifiedDeltas)
        {
            if (string.Equals(candidate.Path, path, StringComparison.Ordinal))
            {
                match = candidate;
            }
        }

        return match;
    }
}
