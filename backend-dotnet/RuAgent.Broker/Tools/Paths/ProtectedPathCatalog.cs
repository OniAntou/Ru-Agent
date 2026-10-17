namespace RuAgent.Broker.Tools.Paths;

/// <summary>
/// The catalog-protected paths: the harness's own key material and runtime
/// descriptors, plus any paths the user adds through
/// <see cref="ExtraPathsEnvironmentVariable"/>. Credential stores and developer
/// files (.ssh, .env, .git, cloud CLI homes) are ordinary paths: they follow the
/// preset profile like any other target, matching the mainstream agents that
/// read them through normal permission rules. Plan and standard deny the
/// catalog entries outright; full_access is literally full and treats them like
/// any other target.
/// </summary>
public static class ProtectedPathCatalog
{
    public const string Version = "2";

    /// <summary>Semicolon-separated absolute paths that join the catalog-protected set.</summary>
    public const string ExtraPathsEnvironmentVariable = "RU_PROTECTED_PATHS";

    /// <summary>Environment variables the extra-path rules resolve against.</summary>
    public delegate string? EnvironmentAccessor(string name);

    private static readonly string[] ProtectedFileNames =
    [
        "runtime_descriptor.json",
        "broker.json",
    ];

    private static readonly string[] ProtectedFilePrefixes =
    [
        "agent_harness",
        "harness_key",
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

        var fileName = segments.Length > 0 ? segments[^1] : string.Empty;
        // Win32 opens `runtime_descriptor.json.` as `runtime_descriptor.json` when
        // the file exists, and the resolver keeps the raw leaf for a missing
        // create target: normalize trailing dots and spaces before matching so
        // the alias cannot slip past.
        var normalizedFileName = fileName.TrimEnd('.', ' ');

        foreach (var prefix in ProtectedFilePrefixes)
        {
            if (normalizedFileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var protectedName in ProtectedFileNames)
        {
            if (string.Equals(normalizedFileName, protectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return IsExtraProtected(normalized, environment);
    }

    private static bool IsExtraProtected(string normalized, EnvironmentAccessor environment)
    {
        var configured = environment(ExtraPathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        foreach (var rawEntry in configured.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string root;
            try
            {
                root = Path.GetFullPath(rawEntry.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsAtOrBelow(normalized, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAtOrBelow(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
