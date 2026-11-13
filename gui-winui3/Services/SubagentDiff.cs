namespace RuApp.Services;

internal enum SubagentDiffOp { Context, Removed, Added }

/// <summary>One diff line. <paramref name="Text"/> keeps a trailing '\r' so a CRLF file round-trips.</summary>
internal sealed record SubagentDiffLine(SubagentDiffOp Op, string Text, bool NoNewlineAtEnd);

internal sealed record SubagentDiffHunk(int OldStart, int OldCount, int NewStart, int NewCount,
    IReadOnlyList<SubagentDiffLine> Lines);

internal sealed record SubagentFileDiff(IReadOnlyList<SubagentDiffHunk> Hunks, int Added, int Removed);

/// <summary>
/// Line diff for the read-only change review, grouped into hunks with context.
/// Lines are split on '\n' only and compared exactly, so the result describes the
/// real bytes of both sides: a unified patch built from it applies to CRLF files
/// and to files without a final newline.
/// </summary>
internal static class SubagentDiff
{
    internal const int ContextLines = 3;
    // The LCS table covers only the region between the common prefix and suffix,
    // so a small edit in a large file stays cheap; a rewrite beyond this is refused.
    internal const long MaxLcsCells = 4_000_000;

    private readonly record struct Line(string Text, bool NoNewlineAtEnd);

    /// <summary>Null when the changed region is too large to diff within the bound.</summary>
    internal static SubagentFileDiff? Compute(string? before, string? after)
    {
        var oldLines = Split(before);
        var newLines = Split(after);
        int prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix]) prefix++;
        int suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix
            && oldLines[oldLines.Count - 1 - suffix] == newLines[newLines.Count - 1 - suffix]) suffix++;
        int m = oldLines.Count - prefix - suffix;
        int n = newLines.Count - prefix - suffix;
        if ((long)m * n > MaxLcsCells) return null;

        var ops = new List<SubagentDiffLine>(oldLines.Count + n);
        for (int i = 0; i < prefix; i++) ops.Add(Op(SubagentDiffOp.Context, oldLines[i]));
        AppendMiddle(ops, oldLines, newLines, prefix, m, n);
        for (int i = oldLines.Count - suffix; i < oldLines.Count; i++) ops.Add(Op(SubagentDiffOp.Context, oldLines[i]));
        return new(Hunks(ops), ops.Count(line => line.Op == SubagentDiffOp.Added),
            ops.Count(line => line.Op == SubagentDiffOp.Removed));
    }

    private static void AppendMiddle(List<SubagentDiffLine> ops, List<Line> oldLines, List<Line> newLines,
        int offset, int m, int n)
    {
        var lcs = new int[m + 1, n + 1];
        for (int i = m - 1; i >= 0; i--)
            for (int j = n - 1; j >= 0; j--)
                lcs[i, j] = oldLines[offset + i] == newLines[offset + j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int a = 0, b = 0;
        while (a < m || b < n)
        {
            if (a < m && b < n && oldLines[offset + a] == newLines[offset + b])
            {
                ops.Add(Op(SubagentDiffOp.Context, oldLines[offset + a]));
                a++;
                b++;
            }
            // Removals first, as in a conventional unified diff.
            else if (a < m && (b == n || lcs[a + 1, b] >= lcs[a, b + 1])) ops.Add(Op(SubagentDiffOp.Removed, oldLines[offset + a++]));
            else ops.Add(Op(SubagentDiffOp.Added, newLines[offset + b++]));
        }
    }

    private static List<SubagentDiffHunk> Hunks(List<SubagentDiffLine> ops)
    {
        var hunks = new List<SubagentDiffHunk>();
        int oldLine = 1, newLine = 1, index = 0;
        while (index < ops.Count)
        {
            if (ops[index].Op == SubagentDiffOp.Context)
            {
                index++;
                oldLine++;
                newLine++;
                continue;
            }

            int start = Math.Max(0, index - ContextLines);
            int oldStart = oldLine - (index - start);
            int newStart = newLine - (index - start);
            int end = index;
            int lastChange = index;
            // Extend while the next change is close enough to share context.
            while (end < ops.Count && end - lastChange <= ContextLines * 2)
            {
                if (ops[end].Op != SubagentDiffOp.Context) lastChange = end;
                end++;
            }

            end = Math.Min(ops.Count, lastChange + 1 + ContextLines);
            var lines = ops.GetRange(start, end - start);
            int oldCount = lines.Count(line => line.Op != SubagentDiffOp.Added);
            int newCount = lines.Count(line => line.Op != SubagentDiffOp.Removed);
            hunks.Add(new(oldCount == 0 ? 0 : oldStart, oldCount, newCount == 0 ? 0 : newStart, newCount, lines));
            for (; index < end; index++)
            {
                if (ops[index].Op != SubagentDiffOp.Added) oldLine++;
                if (ops[index].Op != SubagentDiffOp.Removed) newLine++;
            }
        }

        return hunks;
    }

    private static SubagentDiffLine Op(SubagentDiffOp op, Line line) => new(op, line.Text, line.NoNewlineAtEnd);

    private static List<Line> Split(string? text)
    {
        var lines = new List<Line>();
        if (string.IsNullOrEmpty(text)) return lines;
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add(new(text[start..], true));
                break;
            }

            lines.Add(new(text[start..end], false));
            start = end + 1;
        }

        return lines;
    }
}
