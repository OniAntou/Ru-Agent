namespace RuAgent.Broker.Tools;

public sealed class WorkspacePathGuard
{
    public WorkspacePathGuard(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException(Root);
        }
    }

    public string Root { get; }

    public string Resolve(string relativePath, bool allowMissing = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\0'))
        {
            throw new UnauthorizedAccessException("workspace_path_invalid");
        }

        var candidate = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!IsWithinRoot(candidate))
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        if (!allowMissing && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new FileNotFoundException("workspace_path_missing", candidate);
        }

        EnsureNoSymlinkEscape(candidate);
        return candidate;
    }

    public string ToRelative(string fullPath)
    {
        var resolved = Path.GetFullPath(fullPath);
        if (!IsWithinRoot(resolved))
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        return Path.GetRelativePath(Root, resolved);
    }

    /// <summary>
    /// The one workspace-relative form every file adapter reports to the model.
    /// Separators are normalized to <c>/</c> and the root itself renders as
    /// <c>.</c>, so the same path reads identically whichever tool touched it.
    /// </summary>
    public static string ToWorkspaceRelative(string canonicalRoot, string canonicalPath)
    {
        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        if (relative == ".")
        {
            return ".";
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private bool IsWithinRoot(string candidate)
    {
        var rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;
        return string.Equals(candidate, Root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureNoSymlinkEscape(string candidate)
    {
        var relative = Path.GetRelativePath(Root, candidate);
        var current = Root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.LinkTarget is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(true)?.FullName;
            if (target is null || !IsWithinRoot(Path.GetFullPath(target)))
            {
                throw new UnauthorizedAccessException("workspace_symlink_escape");
            }
        }
    }
}
