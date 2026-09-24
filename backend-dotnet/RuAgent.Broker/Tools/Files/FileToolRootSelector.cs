using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Files;

internal static class FileToolRootSelector
{
    internal static string SelectRoot(
        CanonicalPathResolver leaseResolver,
        string leaseRoot,
        IReadOnlyList<string> grantedDirectories,
        string pathInput) =>
        SelectRootCore(leaseResolver, leaseRoot, grantedDirectories, null, pathInput);

    internal static string SelectRoot(
        CanonicalPathResolver leaseResolver,
        string leaseRoot,
        IReadOnlyList<string> grantedDirectories,
        AgentPolicyProfile profile,
        string pathInput) =>
        SelectRootCore(leaseResolver, leaseRoot, grantedDirectories, profile, pathInput);

    internal static string SelectRoot(
        CanonicalPathResolver leaseResolver,
        string leaseRoot,
        IReadOnlyList<string> grantedDirectories,
        string pathInput,
        AgentPolicyProfile profile) =>
        SelectRootCore(leaseResolver, leaseRoot, grantedDirectories, profile, pathInput);

    private static string SelectRootCore(
        CanonicalPathResolver leaseResolver,
        string leaseRoot,
        IReadOnlyList<string> grantedDirectories,
        AgentPolicyProfile? profile,
        string pathInput)
    {
        ArgumentNullException.ThrowIfNull(leaseResolver);
        ArgumentNullException.ThrowIfNull(grantedDirectories);
        var resolved = leaseResolver.Resolve(pathInput);
        if (resolved.IsProtected)
        {
            throw new UnauthorizedAccessException("protected_path");
        }

        var canonical = resolved.CanonicalPath;
        if (IsWithin(canonical, leaseRoot))
        {
            return leaseRoot;
        }

        if (IsFullAccess(profile))
        {
            var ancestor = FindNearestExistingDirectory(canonical);
            if (ancestor is not null)
            {
                return ancestor;
            }
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

    private static bool IsFullAccess(AgentPolicyProfile? profile) =>
        profile is not null
        && string.Equals(
            profile.Preset,
            AgentPolicyProfileCatalog.FullAccessPresetWireValue,
            StringComparison.Ordinal)
        && profile.DecisionFor(AgentPolicySubject.ExternalStructuredFile) == AgentPolicyDecision.Allow;

    private static string? FindNearestExistingDirectory(string canonicalPath)
    {
        var current = Directory.Exists(canonicalPath)
            ? canonicalPath
            : Path.GetDirectoryName(canonicalPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                return Path.GetFullPath(current);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return null;
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
