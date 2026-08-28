using System.Collections.Concurrent;

namespace RuBackend.Harness.Reconciliation;

/// <summary>
/// Bridge between the V1 approvals endpoint and the per-run tool gateway: an approved
/// pending ask consumes exactly one matching intent for that run, while an approved
/// external directory remains bounded to that run for the short-lived continuation.
/// Process-local by design; a restart drops grants and the run re-enters reconciliation
/// instead. The argument hash is retained so an approval for one model proposal cannot
/// be replayed against a different tool or argument payload after the run resumes.
/// </summary>
public interface IAgentApprovalGrants
{
    void GrantNext(string runId, string intentHash);

    bool TryConsume(string runId, string intentHash);

    /// <summary>
    /// Grants the current run access to one canonical directory outside its trusted
    /// workspace. The grant is deliberately run-scoped and process-local; durable
    /// project scope is represented by a persisted policy rule.
    /// </summary>
    void GrantExternalDirectory(string runId, string canonicalDirectory);

    bool IsExternalDirectoryGranted(string runId, string canonicalPath);
}

public sealed class InMemoryAgentApprovalGrants : IAgentApprovalGrants
{
    private static readonly TimeSpan GrantTtl = TimeSpan.FromMinutes(30);
    private const int MaxExternalDirectoriesPerRun = 64;
    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _externalDirectories = new(StringComparer.Ordinal);

    public void GrantNext(string runId, string intentHash)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("run_id is required", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(intentHash))
        {
            throw new ArgumentException("intent_hash is required", nameof(intentHash));
        }

        var now = DateTimeOffset.UtcNow;
        Prune(now);
        _grants.TryAdd(runId, new Grant(intentHash, now));
    }

    public bool TryConsume(string runId, string intentHash)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(intentHash))
        {
            return false;
        }

        if (!_grants.TryGetValue(runId, out var grant))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - grant.CreatedAt >= GrantTtl)
        {
            TryRemoveExact(runId, grant);
            return false;
        }

        // A mismatched proposal must not consume the valid grant; the model may
        // still re-emit the approved call after a transient worker retry.
        if (!string.Equals(grant.IntentHash, intentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return TryRemoveExact(runId, grant);
    }

    public void GrantExternalDirectory(string runId, string canonicalDirectory)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("run_id is required", nameof(runId));
        }

        var directory = NormalizeDirectory(canonicalDirectory);
        var now = DateTimeOffset.UtcNow;
        var grants = _externalDirectories.GetOrAdd(
            runId,
            _ => new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

        if (!grants.ContainsKey(directory) && grants.Count >= MaxExternalDirectoriesPerRun)
        {
            foreach (var oldest in grants.OrderBy(pair => pair.Value).Take(grants.Count - MaxExternalDirectoriesPerRun + 1))
            {
                grants.TryRemove(new KeyValuePair<string, DateTimeOffset>(oldest.Key, oldest.Value));
            }
        }

        grants[directory] = now;
        PruneExternalDirectories(now);
    }

    public bool IsExternalDirectoryGranted(string runId, string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(canonicalPath)
            || !_externalDirectories.TryGetValue(runId, out var grants))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = NormalizePath(canonicalPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var grant in grants)
        {
            if (now - grant.Value >= GrantTtl)
            {
                grants.TryRemove(new KeyValuePair<string, DateTimeOffset>(grant.Key, grant.Value));
                continue;
            }

            if (IsWithinDirectory(candidate, grant.Key))
            {
                return true;
            }
        }

        if (grants.IsEmpty)
        {
            _externalDirectories.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, DateTimeOffset>>(runId, grants));
        }

        return false;
    }

    private void Prune(DateTimeOffset now)
    {
        if (_grants.Count < 1024)
        {
            return;
        }

        foreach (var pair in _grants)
        {
            if (now - pair.Value.CreatedAt >= GrantTtl)
            {
                TryRemoveExact(pair.Key, pair.Value);
            }
        }
    }

    private void PruneExternalDirectories(DateTimeOffset now)
    {
        if (_externalDirectories.Count < 1024)
        {
            return;
        }

        foreach (var run in _externalDirectories)
        {
            foreach (var grant in run.Value)
            {
                if (now - grant.Value >= GrantTtl)
                {
                    run.Value.TryRemove(new KeyValuePair<string, DateTimeOffset>(grant.Key, grant.Value));
                }
            }

            if (run.Value.IsEmpty)
            {
                _externalDirectories.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, DateTimeOffset>>(run.Key, run.Value));
            }
        }
    }

    private static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)
            || directory.Contains('\0')
            || directory.Contains('*')
            || directory.Contains('?'))
        {
            throw new ArgumentException("canonical directory is invalid", nameof(directory));
        }

        var fullPath = NormalizePath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A filesystem root cannot be granted as an external directory.", nameof(directory));
        }

        return fullPath;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new ArgumentException("canonical path is invalid", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsWithinDirectory(string candidate, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(directory);
        return string.Equals(candidate, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && candidate.StartsWith(normalizedDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryRemoveExact(string runId, Grant grant) =>
        ((ICollection<KeyValuePair<string, Grant>>)_grants)
            .Remove(new KeyValuePair<string, Grant>(runId, grant));

    private sealed record Grant(string IntentHash, DateTimeOffset CreatedAt);
}
