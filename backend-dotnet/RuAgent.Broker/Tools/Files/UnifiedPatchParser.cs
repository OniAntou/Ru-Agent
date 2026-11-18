using System.Text;

namespace RuAgent.Broker.Tools.Files;

public sealed record PatchHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<string> Lines);

public sealed record ParsedPatchFile(
    string TargetPath,
    string? SourcePath,
    IReadOnlyList<PatchHunk> Hunks,
    bool IsNewFile,
    bool IsDeletedFile)
{
    /// <summary>
    /// The file moves from <see cref="SourcePath"/> to <see cref="TargetPath"/>: the source is
    /// deleted and the target created with the source content after the hunks (none for a pure
    /// rename). Every authority check treats it as exactly that delete plus that create.
    /// </summary>
    public bool IsRename =>
        !IsNewFile && !IsDeletedFile && SourcePath is not null
        && !string.Equals(SourcePath, TargetPath, StringComparison.Ordinal);
}

public static partial class UnifiedPatchParser
{
    public const int MaxPatchBytes = 4 * 1024 * 1024;
    public const int MaxFiles = 64;
    public const int MaxHunksPerFile = 256;
    public const int MaxLinesPerHunk = 4096;

    public static IReadOnlyList<ParsedPatchFile> Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var byteCount = Encoding.UTF8.GetByteCount(patch);
        if (byteCount > MaxPatchBytes)
        {
            throw new ArgumentException("patch_too_large");
        }

        if (string.IsNullOrWhiteSpace(patch))
        {
            throw new ArgumentException("patch_empty");
        }

        var lines = SplitLines(patch);
        var files = new List<ParsedPatchFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        // Git's "rename from"/"rename to" pair: either the next ---/+++ header repeats it
        // (a rename with edits), or the file section ends without one (a pure rename).
        string? renameFrom = null;
        string? renameTo = null;
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushPureRename();
                i++;
                continue;
            }

            if (line.StartsWith("index ", StringComparison.Ordinal)
                || IsIgnoredGitHeader(line))
            {
                i++;
                continue;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                if (renameFrom is not null) throw new ArgumentException("invalid_patch_header");
                renameFrom = NormalizePatchPath(line["rename from ".Length..]);
                i++;
                continue;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                if (renameTo is not null) throw new ArgumentException("invalid_patch_header");
                renameTo = NormalizePatchPath(line["rename to ".Length..]);
                i++;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (i + 1 >= lines.Count || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    throw new ArgumentException("invalid_patch_header");
                }

                var sourceRaw = line.Substring(4).Trim();
                var targetRaw = lines[i + 1].Substring(4).Trim();
                var sourcePath = NormalizePatchPath(sourceRaw);
                var targetPath = NormalizePatchPath(targetRaw);
                bool isNewFile = string.Equals(sourceRaw, "/dev/null", StringComparison.Ordinal) || string.Equals(sourcePath, "/dev/null", StringComparison.Ordinal);
                bool isDeleted = string.Equals(targetRaw, "/dev/null", StringComparison.Ordinal) || string.Equals(targetPath, "/dev/null", StringComparison.Ordinal);

                string effectivePath;
                if (isDeleted)
                {
                    effectivePath = sourcePath;
                    if (string.IsNullOrWhiteSpace(effectivePath) || effectivePath == "/dev/null")
                    {
                        throw new ArgumentException("invalid_patch_path");
                    }
                }
                else
                {
                    effectivePath = targetPath;
                }

                ValidatePath(effectivePath);
                var isRename = !isNewFile && !isDeleted && !string.Equals(sourcePath, targetPath, StringComparison.Ordinal);
                if (renameFrom is not null || renameTo is not null)
                {
                    // The git rename lines must name the same move as the header.
                    if (!isRename
                        || !string.Equals(renameFrom, sourcePath, StringComparison.Ordinal)
                        || !string.Equals(renameTo, targetPath, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("invalid_patch_header");
                    }

                    renameFrom = null;
                    renameTo = null;
                }

                if (isRename)
                {
                    ValidateRenameSource(sourcePath, effectivePath);
                }

                if (!seenPaths.Add(effectivePath))
                {
                    throw new ArgumentException("patch_duplicate_path");
                }

                if (files.Count >= MaxFiles)
                {
                    throw new ArgumentException("patch_too_many_files");
                }

                i += 2;
                var hunks = new List<PatchHunk>();
                while (i < lines.Count && lines[i].StartsWith("@@ ", StringComparison.Ordinal))
                {
                    var hunkHeader = lines[i];
                    var (oldStart, oldCount, newStart, newCount) = ParseHunkHeader(hunkHeader);
                    i++;
                    var hunkLines = new List<string>();
                    while (i < lines.Count
                        && !lines[i].StartsWith("--- ", StringComparison.Ordinal)
                        && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal)
                        && !lines[i].StartsWith("@@ ", StringComparison.Ordinal))
                    {
                        var hl = lines[i];
                        if (hl.Length == 0)
                        {
                            hl = " ";
                        }

                        var prefix = hl[0];
                        if (prefix != ' ' && prefix != '+' && prefix != '-' && prefix != '\\')
                        {
                            break;
                        }

                        hunkLines.Add(hl);
                        if (hunkLines.Count > MaxLinesPerHunk)
                        {
                            throw new ArgumentException("patch_hunk_too_large");
                        }

                        i++;
                    }

                    if (hunks.Count >= MaxHunksPerFile)
                    {
                        throw new ArgumentException("patch_too_many_hunks");
                    }

                    hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, hunkLines));
                }

                files.Add(new ParsedPatchFile(effectivePath, sourcePath, hunks, isNewFile, isDeleted));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            throw new ArgumentException("invalid_patch_format");
        }

        FlushPureRename();
        if (files.Count == 0)
        {
            throw new ArgumentException("patch_no_files");
        }

        return files;

        void FlushPureRename()
        {
            if (renameFrom is null && renameTo is null) return;
            if (renameFrom is null || renameTo is null || renameFrom == "/dev/null" || renameTo == "/dev/null")
                throw new ArgumentException("invalid_patch_header");
            ValidatePath(renameTo);
            if (string.Equals(renameFrom, renameTo, StringComparison.Ordinal)) throw new ArgumentException("invalid_patch_header");
            ValidateRenameSource(renameFrom, renameTo);
            if (!seenPaths.Add(renameTo)) throw new ArgumentException("patch_duplicate_path");
            if (files.Count >= MaxFiles) throw new ArgumentException("patch_too_many_files");
            files.Add(new ParsedPatchFile(renameTo, renameFrom, [], IsNewFile: false, IsDeletedFile: false));
            renameFrom = null;
            renameTo = null;
        }

        void ValidateRenameSource(string source, string target)
        {
            ValidatePath(source);
            // Canonical paths compare case-insensitively, so a case-only move would delete
            // the file it just created.
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("patch_rename_case_only");
            if (!seenPaths.Add(source)) throw new ArgumentException("patch_duplicate_path");
        }
    }

    /// <summary>Git extended header lines that carry nothing Ru applies (modes, similarity).</summary>
    private static bool IsIgnoredGitHeader(string line) =>
        line.StartsWith("similarity index ", StringComparison.Ordinal)
        || line.StartsWith("dissimilarity index ", StringComparison.Ordinal)
        || line.StartsWith("old mode ", StringComparison.Ordinal)
        || line.StartsWith("new mode ", StringComparison.Ordinal)
        || line.StartsWith("new file mode ", StringComparison.Ordinal)
        || line.StartsWith("deleted file mode ", StringComparison.Ordinal);

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new ArgumentException("invalid_patch_path");
        }

        if (path.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }
    }

    private static void RequireOriginalLine(List<string> originalLines, int index, string expected)
    {
        if (index >= originalLines.Count
            || !string.Equals(originalLines[index], expected, StringComparison.Ordinal))
        {
            throw new ArgumentException("patch_context_mismatch");
        }
    }

    private static (int oldStart, int oldCount, int newStart, int newCount) ParseHunkHeader(string header)
    {
        try
        {
            var withoutAt = header.Trim();
            if (!withoutAt.StartsWith("@@ ", StringComparison.Ordinal))
            {
                throw new ArgumentException("invalid_hunk_header");
            }

            var end = withoutAt.IndexOf(" @@", StringComparison.Ordinal);
            if (end < 0)
            {
                throw new ArgumentException("invalid_hunk_header");
            }

            var rangePart = withoutAt.Substring(3, end - 3).Trim();
            var parts = rangePart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                throw new ArgumentException("invalid_hunk_header");
            }

            var oldPart = parts[0];
            var newPart = parts[1];
            if (!oldPart.StartsWith("-", StringComparison.Ordinal) || !newPart.StartsWith("+", StringComparison.Ordinal))
            {
                throw new ArgumentException("invalid_hunk_header");
            }

            oldPart = oldPart.Substring(1);
            newPart = newPart.Substring(1);
            var oldTokens = oldPart.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var newTokens = newPart.Split(',', StringSplitOptions.RemoveEmptyEntries);
            int oldStart = int.Parse(oldTokens[0]);
            int oldCount = oldTokens.Length > 1 ? int.Parse(oldTokens[1]) : 1;
            int newStart = int.Parse(newTokens[0]);
            int newCount = newTokens.Length > 1 ? int.Parse(newTokens[1]) : 1;
            if (oldStart < 0 || newStart < 0 || oldCount < 0 || newCount < 0)
            {
                throw new ArgumentException("invalid_hunk_header");
            }

            return (oldStart, oldCount, newStart, newCount);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ArgumentException("invalid_hunk_header", ex);
        }
    }

    private static string NormalizePatchPath(string raw)
    {
        var trimmed = raw.Trim();
        var tabIdx = trimmed.IndexOf('\t');
        if (tabIdx >= 0)
        {
            trimmed = trimmed.Substring(0, tabIdx).Trim();
        }

        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(2);
        }

        trimmed = trimmed.Replace('\\', '/').Trim();
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(2);
        }

        return trimmed;
    }

    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            result.Add(line);
        }

        return result;
    }

    private static List<string> SplitContentLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new List<string>();
        }

        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        var parts = normalized.Split('\n');
        if (parts.Length > 0 && parts[parts.Length - 1] == string.Empty && normalized.EndsWith("\n", StringComparison.Ordinal))
        {
            var list = parts.ToList();
            list.RemoveAt(list.Count - 1);
            return list;
        }

        return parts.ToList();
    }
}
