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
    bool IsDeletedFile);

public static class UnifiedPatchParser
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
        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
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

                if (string.IsNullOrWhiteSpace(effectivePath))
                {
                    throw new ArgumentException("invalid_patch_path");
                }

                if (effectivePath.Contains('\0'))
                {
                    throw new ArgumentException("invalid_patch_path");
                }

                if (effectivePath.Length > FileToolLimits.MaxPathLength)
                {
                    throw new ArgumentException("path_too_long");
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

                        if (hl.StartsWith("\\ No newline", StringComparison.Ordinal))
                        {
                            i++;
                            continue;
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

        if (files.Count == 0)
        {
            throw new ArgumentException("patch_no_files");
        }

        return files;
    }

    public static string ApplyHunks(string originalContent, IReadOnlyList<PatchHunk> hunks)
    {
        var originalLines = SplitContentLines(originalContent);
        var newLines = new List<string>();
        int origIdx = 0;

        foreach (var hunk in hunks)
        {
            int oldStartIdx = Math.Max(0, hunk.OldStart - 1);
            while (origIdx < oldStartIdx && origIdx < originalLines.Count)
            {
                newLines.Add(originalLines[origIdx]);
                origIdx++;
            }

            int oldLinesConsumed = 0;
            foreach (var hl in hunk.Lines)
            {
                if (hl.Length == 0)
                {
                    continue;
                }

                var prefix = hl[0];
                var content = hl.Length > 1 ? hl.Substring(1) : string.Empty;
                switch (prefix)
                {
                    case ' ':
                        RequireOriginalLine(originalLines, origIdx, content);
                        newLines.Add(content);
                        origIdx++;
                        oldLinesConsumed++;
                        break;
                    case '-':
                        RequireOriginalLine(originalLines, origIdx, content);
                        origIdx++;
                        oldLinesConsumed++;
                        break;
                    case '+':
                        newLines.Add(content);
                        break;
                    case '\\':
                        break;
                }
            }

            // The hunk header declares how many old-side lines the hunk covers;
            // a short or long old side means the patch does not describe the file
            // it claims to patch.
            if (oldLinesConsumed != hunk.OldCount)
            {
                throw new ArgumentException("patch_context_mismatch");
            }
        }

        while (origIdx < originalLines.Count)
        {
            newLines.Add(originalLines[origIdx]);
            origIdx++;
        }

        return string.Join("\n", newLines);
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
