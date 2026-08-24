namespace RuAgent.Broker.Tools.Paths;

public static class ProtectedPathCatalog
{
    public const string Version = "1";

    public static bool IsProtected(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            return false;
        }

        var normalized = Path.GetFullPath(canonicalPath);

        // Check .git segment: any segment equals ".git" (case-sensitive, but also case-insensitive fallback)
        var segments = normalized.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // .env* files: segment starts with .env
            if (segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Also check file name explicitly for .env* in case path ends with file
        var fileName = Path.GetFileName(normalized);
        if (!string.IsNullOrEmpty(fileName) && fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Agent Harness database/key/descriptor/rule locations
        // These are protected regardless of workspace location.
        // Use case-insensitive file name checks.
        var lower = normalized.ToLowerInvariant();
        if (lower.EndsWith("agent_harness.db", StringComparison.Ordinal) ||
            lower.EndsWith("harness_key.dat", StringComparison.Ordinal) ||
            lower.EndsWith("runtime_descriptor.json", StringComparison.Ordinal) ||
            lower.EndsWith("broker.json", StringComparison.Ordinal) ||
            lower.Contains("agent_harness", StringComparison.Ordinal) ||
            lower.Contains("harness_key", StringComparison.Ordinal))
        {
            return true;
        }

        // Descriptor and key may also appear as "agent_" rule files; treat any file containing "runtime_descriptor" or "broker.json" already covered.

        // Ru credential locations: Windows Credential Manager is not a file, but we treat
        // common credential-related file patterns as protected.
        // This includes any path that contains "credentials" or is under a business data folder with secrets.
        if (lower.Contains("credentials", StringComparison.Ordinal) ||
            lower.Contains("credential", StringComparison.Ordinal))
        {
            return true;
        }

        // Additional: protect any file that looks like a key/descriptor in BusinessDataRoot
        // We already cover specific file names; this is defense in depth.

        return false;
    }
}
