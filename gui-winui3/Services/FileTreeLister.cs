using RuApp.Models;

namespace RuApp.Services;

/// <summary>One directory level's listing and whether the cap cut it short.</summary>
public sealed record FileTreeListing(IReadOnlyList<FileTreeRow> Rows, bool Truncated);

/// <summary>
/// Lists one directory level for the side panel's Files tab: folders first,
/// then files, ordinal case-insensitive, bounded per directory. The skip sets
/// mirror the CLI's project-file scan (<c>ChatProjectFileWorkflow</c>), so the
/// two surfaces hide the same build trees and credential-name files.
/// </summary>
public static class FileTreeLister
{
    public const int MaxEntriesPerDirectory = 256;

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".ru", ".venv", "venv", "node_modules", "bin", "obj", "packages", "artifacts",
        "target", "dist", "build", "coverage", ".cache", ".ssh", "secrets", "credentials",
    };

    private static readonly HashSet<string> SkippedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".env.local", ".npmrc", ".netrc", "id_rsa", "id_ed25519", "credentials", "secrets",
    };

    public static bool IsSkipped(string name, bool isDirectory) =>
        isDirectory ? SkippedDirectoryNames.Contains(name) : SkippedFileNames.Contains(name);

    public static FileTreeListing List(string directoryPath, int depth, int maxEntries = MaxEntriesPerDirectory)
    {
        var entries = new List<FileTreeRow>();
        bool truncated = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            string name;
            bool isDirectory;
            bool canExpand;
            try
            {
                var attributes = File.GetAttributes(path);
                name = Path.GetFileName(path);
                isDirectory = (attributes & FileAttributes.Directory) != 0;
                canExpand = isDirectory && (attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                System.Security.SecurityException)
            {
                continue;
            }

            if (IsSkipped(name, isDirectory))
            {
                continue;
            }

            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            entries.Add(new FileTreeRow(name, path, isDirectory, canExpand, depth));
        }

        entries.Sort(Compare);
        return new FileTreeListing(entries, truncated);
    }

    private static int Compare(FileTreeRow left, FileTreeRow right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }
}
