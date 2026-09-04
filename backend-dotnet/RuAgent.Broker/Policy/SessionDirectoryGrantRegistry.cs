using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Policy;

public sealed record CanonicalDirectoryIdentity
{
    private CanonicalDirectoryIdentity(string canonicalPath)
    {
        CanonicalPath = canonicalPath;
    }

    public string CanonicalPath { get; }

    public static CanonicalDirectoryIdentity CreateExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        return new CanonicalDirectoryIdentity(
            TrimTrailing(new CanonicalPathResolver(fullPath).CanonicalRoot));
    }

    internal static string NormalizeTarget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TrimTrailing(new CanonicalPathResolver(Path.GetFullPath(path)).CanonicalRoot);
    }

    private static string TrimTrailing(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public interface ISessionDirectoryGrantRegistry
{
    bool Contains(string sessionId, string canonicalTarget);
    void Add(string sessionId, CanonicalDirectoryIdentity directory);
    void RevokeSession(string sessionId);
}

/// <summary>
/// Process-memory authority for external directory boundaries. Entries are isolated by
/// Agent session and intentionally have no persistence representation.
/// </summary>
public sealed class SessionDirectoryGrantRegistry : ISessionDirectoryGrantRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _bySession = new(StringComparer.Ordinal);

    public bool Contains(string sessionId, string canonicalTarget)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(canonicalTarget))
        {
            return false;
        }

        var target = CanonicalDirectoryIdentity.NormalizeTarget(canonicalTarget);
        lock (_gate)
        {
            return _bySession.TryGetValue(sessionId, out var directories)
                   && directories.Any(directory => IsWithin(target, directory));
        }
    }

    public void Add(string sessionId, CanonicalDirectoryIdentity directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(directory);
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var directories))
            {
                directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _bySession.Add(sessionId, directories);
            }

            directories.Add(directory.CanonicalPath);
        }
    }

    public void RevokeSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_gate)
        {
            _bySession.Remove(sessionId);
        }
    }

    private static bool IsWithin(string candidate, string directory)
    {
        if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            || directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
