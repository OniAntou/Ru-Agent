using System.IO;

namespace RuApp.Services;

/// <summary>
/// File helpers for the Changes tab: which rows are agent scratch files, and where a
/// row's workspace-relative path lands on disk. Pure except for the existence check,
/// so the rules are testable.
/// </summary>
public static class ChangesTabFiles
{
    private static readonly string[] ScratchPrefixes = [".tmp", "tmp_", "_tmp", "~$"];
    private static readonly string[] ScratchExtensions = [".tmp", ".bak", ".orig", ".rej", ".swp"];

    /// <summary>
    /// True for the throwaway files agents write while working (probe scripts, patch
    /// drafts, editor backups), which crowd out the real changes.
    /// </summary>
    public static bool IsScratch(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        return ScratchPrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || ScratchExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The full path of a workspace-relative path, or null when either is missing or the
    /// result would leave the workspace. The activity already carries only safe segments;
    /// this is the second check, made against the real root.
    /// </summary>
    public static string? Resolve(string? workspace, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
