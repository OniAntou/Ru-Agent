using System;
using System.Collections.Generic;
using System.Globalization;

namespace RuApp.Models
{
    public enum DiffKind
    {
        Unchanged,
        Removed,
        Added
    }

    public sealed record DiffLine(
        int? OldLineNumber,
        int? NewLineNumber,
        string DisplayLineNumber,
        DiffKind Kind,
        string Text)
    {
        public bool IsRemoved => Kind == DiffKind.Removed;
        public bool IsAdded => Kind == DiffKind.Added;
        public bool IsUnchanged => Kind == DiffKind.Unchanged;
    }

    public static class DiffEngine
    {
        /// <summary>
        /// Computes a unified line-by-line diff between <paramref name="before"/> and <paramref name="after"/>.
        /// Preserves surrounding context lines with accurate line numbering starting at <paramref name="startLine"/>.
        /// </summary>
        public static IReadOnlyList<DiffLine> Compute(string? before, string? after, int? startLine = null)
        {
            var oldLines = SplitLines(before);
            var newLines = SplitLines(after);
            int baseLine = Math.Max(1, startLine.GetValueOrDefault(1));

            if (oldLines.Count == 0 && newLines.Count == 0)
            {
                return Array.Empty<DiffLine>();
            }

            if (oldLines.Count == 0)
            {
                var addedOnly = new List<DiffLine>(newLines.Count);
                for (int i = 0; i < newLines.Count; i++)
                {
                    int lineNum = baseLine + i;
                    addedOnly.Add(new DiffLine(null, lineNum, lineNum.ToString(), DiffKind.Added, newLines[i]));
                }
                return addedOnly;
            }

            if (newLines.Count == 0)
            {
                var removedOnly = new List<DiffLine>(oldLines.Count);
                for (int i = 0; i < oldLines.Count; i++)
                {
                    int lineNum = baseLine + i;
                    removedOnly.Add(new DiffLine(lineNum, null, lineNum.ToString(), DiffKind.Removed, oldLines[i]));
                }
                return removedOnly;
            }

            // Compute Longest Common Subsequence (LCS) matrix
            int m = oldLines.Count;
            int n = newLines.Count;
            int[,] lcs = new int[m + 1, n + 1];

            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
                    {
                        lcs[i + 1, j + 1] = lcs[i, j] + 1;
                    }
                    else
                    {
                        lcs[i + 1, j + 1] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                    }
                }
            }

            // Backtrack to build diff in reverse
            var rawList = new List<DiffLine>();
            int currOld = m - 1;
            int currNew = n - 1;

            while (currOld >= 0 || currNew >= 0)
            {
                if (currOld >= 0 && currNew >= 0 && string.Equals(oldLines[currOld], newLines[currNew], StringComparison.Ordinal))
                {
                    rawList.Add(new DiffLine(currOld, currNew, string.Empty, DiffKind.Unchanged, oldLines[currOld]));
                    currOld--;
                    currNew--;
                }
                else if (currNew >= 0 && (currOld < 0 || lcs[currOld + 1, currNew] >= lcs[currOld, currNew + 1]))
                {
                    rawList.Add(new DiffLine(null, currNew, string.Empty, DiffKind.Added, newLines[currNew]));
                    currNew--;
                }
                else if (currOld >= 0)
                {
                    rawList.Add(new DiffLine(currOld, null, string.Empty, DiffKind.Removed, oldLines[currOld]));
                    currOld--;
                }
            }

            rawList.Reverse();

            // Format line numbers based on baseLine offset
            var result = new List<DiffLine>(rawList.Count);
            foreach (var item in rawList)
            {
                int? realOld = item.OldLineNumber.HasValue ? baseLine + item.OldLineNumber.Value : null;
                int? realNew = item.NewLineNumber.HasValue ? baseLine + item.NewLineNumber.Value : null;
                string displayLine = (item.Kind == DiffKind.Removed ? realOld : realNew)?.ToString() ?? string.Empty;

                result.Add(new DiffLine(realOld, realNew, displayLine, item.Kind, item.Text));
            }

            return result;
        }

        /// <summary>
        /// The unified-diff header for a fragment: where the comparison starts and how many
        /// lines it covers on each side, e.g. "@@ -42,3 +42,4 @@". A single-line range drops
        /// the count, and an empty side names the line before it, as `git diff` does.
        /// </summary>
        public static string Header(IReadOnlyList<DiffLine> lines, int startLine = 1)
        {
            ArgumentNullException.ThrowIfNull(lines);
            int oldCount = 0, newCount = 0;
            foreach (var line in lines)
            {
                if (!line.IsAdded) oldCount++;
                if (!line.IsRemoved) newCount++;
            }

            int start = Math.Max(1, startLine);
            return "@@ -" + Range(start, oldCount) + " +" + Range(start, newCount) + " @@";
        }

        private static string Range(int start, int count) => count switch
        {
            0 => Math.Max(0, start - 1).ToString(CultureInfo.InvariantCulture) + ",0",
            1 => start.ToString(CultureInfo.InvariantCulture),
            _ => start.ToString(CultureInfo.InvariantCulture) + "," + count.ToString(CultureInfo.InvariantCulture),
        };

        private static List<string> SplitLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new List<string>(0);
            }

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var raw = normalized.Split('\n');
            return new List<string>(raw);
        }
    }
}
