using System.Collections.Concurrent;

namespace RuBackend.Harness.Workspace;

public sealed record ChangeEntry(
    string Path,
    string InitialHash,
    string BeforeHash,
    string AfterHash,
    DateTimeOffset MutatedAt);

public sealed class RunChangeSetStore
{
    private readonly ConcurrentDictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);

    private sealed class RunRecord
    {
        public readonly Dictionary<string, string> InitialHashes = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ChangeEntry> Mutations = new(StringComparer.Ordinal);
        public readonly object Gate = new();
    }

    public void InitializeRun(string runId, IReadOnlyDictionary<string, string> initialHashes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(initialHashes);
        var record = new RunRecord();
        foreach (var kv in initialHashes)
        {
            var normalized = NormalizePath(kv.Key);
            ValidateHash(kv.Value);
            record.InitialHashes[normalized] = kv.Value;
        }

        _runs[runId] = record;
    }

    public void ObserveInitial(string runId, string relativePath, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ValidateHashOrMissing(hash);
        var record = _runs.GetOrAdd(runId, _ => new RunRecord());
        var normalized = NormalizePath(relativePath);
        lock (record.Gate)
        {
            if (!record.InitialHashes.ContainsKey(normalized))
            {
                record.InitialHashes[normalized] = hash;
            }
        }
    }

    public void RecordMutation(string runId, string relativePath, string beforeHash, string afterHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ValidateHashOrMissing(beforeHash);
        ValidateHashOrMissing(afterHash);
        var normalized = NormalizePath(relativePath);
        var record = _runs.GetOrAdd(runId, _ => new RunRecord());
        lock (record.Gate)
        {
            record.InitialHashes.TryGetValue(normalized, out var initial);
            initial ??= beforeHash;
            var entry = new ChangeEntry(normalized, initial, beforeHash, afterHash, DateTimeOffset.UtcNow);
            record.Mutations[normalized] = entry;
        }
    }

    public IReadOnlyList<ChangeEntry> GetChangeSet(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!_runs.TryGetValue(runId, out var record))
        {
            return Array.Empty<ChangeEntry>();
        }

        lock (record.Gate)
        {
            return record.Mutations.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<string> GetChangedPaths(string runId)
    {
        return GetChangeSet(runId).Select(e => e.Path).ToArray();
    }

    public bool Contains(string runId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!_runs.TryGetValue(runId, out var record))
        {
            return false;
        }

        var normalized = NormalizePath(relativePath);
        lock (record.Gate)
        {
            return record.Mutations.ContainsKey(normalized);
        }
    }

    public void CompleteRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
    }

    public void RemoveRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runs.TryRemove(runId, out _);
    }

    public int RunCount => _runs.Count;

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        return normalized;
    }

    private static void ValidateHash(string hash)
    {
        if (hash == "missing")
        {
            return;
        }

        if (hash.Length != 64 || !IsLowerHex(hash))
        {
            throw new ArgumentException("invalid_hash");
        }
    }

    private static void ValidateHashOrMissing(string hash)
    {
        if (string.Equals(hash, "missing", StringComparison.Ordinal))
        {
            return;
        }

        ValidateHash(hash);
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
