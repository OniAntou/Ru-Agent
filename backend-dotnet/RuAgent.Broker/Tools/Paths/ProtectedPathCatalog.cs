namespace RuAgent.Broker.Tools.Paths;

/// <summary>
/// The hard deny list: paths no preset, grant, or approval may open. Rules are
/// anchored to real credential storage locations and content-bearing file
/// names, not to loose substrings -- a substring match over the whole path
/// both denied legitimate work (any file whose directory name happened to
/// contain the word "credential") and missed the directories that actually
/// hold secrets (.ssh, DPAPI, cloud CLIs).
/// </summary>
public static class ProtectedPathCatalog
{
    public const string Version = "1";

    /// <summary>Environment variables the anchored rules resolve against.</summary>
    public delegate string? EnvironmentAccessor(string name);

    // Credential stores reachable by the terminal on this machine: the SSH key
    // ring, DPAPI master keys and credential blobs, cloud CLI homes, and
    // kube/docker configs. The comparison is anchored to the resolved base
    // directory, so a workspace file named "credentials" stays editable while
    // the real stores never open.
    private static readonly (string EnvName, string Tail)[] SensitiveDirectories =
    [
        ("USERPROFILE", ".ssh"),
        ("APPDATA", @"Microsoft\Credentials"),
        ("APPDATA", @"Microsoft\Protect"),
        ("LOCALAPPDATA", @"Microsoft\Credentials"),
        ("LOCALAPPDATA", @"Microsoft\Vault"),
        ("USERPROFILE", ".aws"),
        ("USERPROFILE", ".azure"),
        ("USERPROFILE", ".gnupg"),
        ("USERPROFILE", ".kube"),
        ("USERPROFILE", ".docker"),
    ];

    private static readonly string[] ProtectedFileNames =
    [
        ".netrc",
        ".git-credentials",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
        "runtime_descriptor.json",
        "broker.json",
    ];

    public static bool IsProtected(string canonicalPath) =>
        IsProtected(canonicalPath, Environment.GetEnvironmentVariable);

    public static bool IsProtected(string canonicalPath, EnvironmentAccessor environment)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            return false;
        }

        var normalized = Path.GetFullPath(canonicalPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        var segments = normalized.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // .git/** at any depth, and .env* files at any depth.
        foreach (var segment in segments)
        {
            if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;

        // Agent harness key/descriptor/database artifacts, wherever they live.
        if (fileName.StartsWith("agent_harness", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("harness_key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var protectedName in ProtectedFileNames)
        {
            if (string.Equals(fileName, protectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Credential stores reachable by the terminal on this machine: the
        // SSH key ring, DPAPI master keys and credential blobs, cloud CLI
        // homes, and kube/docker configs. The comparison is anchored to the
        // resolved base directory, so a workspace file named "credentials"
        // stays editable while the real stores never open.
        foreach (var (envName, tail) in SensitiveDirectories)
        {
            var baseValue = environment(envName);
            if (string.IsNullOrWhiteSpace(baseValue))
            {
                continue;
            }

            var sensitiveRoot = CombineSafe(baseValue, tail);
            if (sensitiveRoot is not null && IsAtOrBelow(normalized, sensitiveRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static string? CombineSafe(string basePath, string tail)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(basePath, tail));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsAtOrBelow(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
