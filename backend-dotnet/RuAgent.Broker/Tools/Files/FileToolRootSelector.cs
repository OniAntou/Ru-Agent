using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// Selects which workspace root a files.* call executes against.
/// Inside the lease root → lease root (behavior unchanged).
/// Outside → the longest matching session-granted directory, else throws
/// <c>outside_workspace</c> exactly as adapters did before. Protected paths
/// are not decided here; the selected toolset's adapters still deny them.
/// Callers must let <see cref="ArgumentException"/> fall through to the
/// lease-rooted adapters so malformed inputs keep their original errors.
/// </summary>
internal static class FileToolRootSelector
{
    internal static string SelectRoot(
        CanonicalPathResolver leaseResolver,
        string leaseRoot,
        IReadOnlyList<string> grantedDirectories,
        string pathInput)
    {
        ArgumentNullException.ThrowIfNull(leaseResolver);
        ArgumentNullException.ThrowIfNull(grantedDirectories);
        var canonical = leaseResolver.Resolve(pathInput).CanonicalPath;
        if (IsWithin(canonical, leaseRoot))
        {
            return leaseRoot;
        }

        string? best = null;
        foreach (var granted in grantedDirectories)
        {
            if (string.IsNullOrWhiteSpace(granted))
            {
                continue;
            }

            if (IsWithin(canonical, granted)
                && (best is null || granted.Length > best.Length))
            {
                best = granted;
            }
        }

        if (best is not null)
        {
            return best;
        }

        throw new UnauthorizedAccessException("outside_workspace");
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
