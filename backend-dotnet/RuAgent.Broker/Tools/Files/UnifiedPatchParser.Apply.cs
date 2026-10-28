namespace RuAgent.Broker.Tools.Files;

public static partial class UnifiedPatchParser
{
    public static string ApplyHunks(string originalContent, IReadOnlyList<PatchHunk> hunks)
    {
        var originalLines = SplitContentLines(originalContent);
        var newLines = new List<string>();
        var originalIndex = 0;
        var finalNewline = originalContent.EndsWith('\n') || originalContent.EndsWith('\r');
        int? unterminatedOutputLine = null;
        foreach (var hunk in hunks)
        {
            var oldIndex = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            var newIndex = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewStart - 1;
            if (oldIndex < originalIndex || oldIndex > originalLines.Count || newIndex < 0)
                throw new ArgumentException("patch_context_mismatch");
            while (originalIndex < oldIndex) newLines.Add(originalLines[originalIndex++]);
            if (newIndex != newLines.Count) throw new ArgumentException("patch_context_mismatch");
            var oldCount = 0;
            var newCount = 0;
            char previousPrefix = '\0';
            foreach (var line in hunk.Lines)
            {
                if (line.Length == 0) throw new ArgumentException("patch_context_mismatch");
                var content = line[1..];
                switch (line[0])
                {
                    case ' ':
                        RequireOriginalLine(originalLines, originalIndex++, content);
                        newLines.Add(content);
                        oldCount++;
                        newCount++;
                        break;
                    case '-':
                        RequireOriginalLine(originalLines, originalIndex++, content);
                        oldCount++;
                        break;
                    case '+':
                        newLines.Add(content);
                        newCount++;
                        break;
                    case '\\':
                        if (line != "\\ No newline at end of file" || previousPrefix is not (' ' or '-' or '+'))
                            throw new ArgumentException("patch_context_mismatch");
                        if (previousPrefix is ' ' or '+') unterminatedOutputLine = newLines.Count;
                        previousPrefix = '\0';
                        continue;
                    default:
                        throw new ArgumentException("patch_context_mismatch");
                }
                previousPrefix = line[0];
            }
            if (oldCount != hunk.OldCount || newCount != hunk.NewCount)
                throw new ArgumentException("patch_context_mismatch");
            if (originalIndex == originalLines.Count) finalNewline = true;
        }
        while (originalIndex < originalLines.Count) newLines.Add(originalLines[originalIndex++]);
        if (unterminatedOutputLine is { } last)
        {
            if (last != newLines.Count) throw new ArgumentException("patch_context_mismatch");
            finalNewline = false;
        }
        if (newLines.Count == 0) return string.Empty;
        var newline = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : originalContent.Contains('\r') && !originalContent.Contains('\n') ? "\r" : "\n";
        return string.Join(newline, newLines) + (finalNewline ? newline : "");
    }
}
