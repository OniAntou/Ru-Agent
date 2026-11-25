using System;
using System.Collections.Generic;

namespace RuApp.Models
{
    /// <summary>A changed character range inside one diff line.</summary>
    public readonly record struct DiffSpan(int Start, int Length);

    /// <summary>
    /// Marks what changed inside a line. A run of removed lines followed by a run of
    /// added lines pairs line by line; each pair is compared word by word and only the
    /// differing words are returned. Pairs too long, too different or unpaired get no
    /// marks, so their whole-line tint stands alone instead of a confetti of ranges.
    /// </summary>
    public static class IntraLineDiff
    {
        /// <summary>Lines longer than this keep only their whole-line tint.</summary>
        public const int MaxLineChars = 1000;

        /// <summary>A pair is marked only when at least this share of its text is unchanged.</summary>
        public const double MinSharedRatio = 0.4;

        private static readonly IReadOnlyList<DiffSpan> None = Array.Empty<DiffSpan>();

        public static IReadOnlyList<IReadOnlyList<DiffSpan>> Mark(IReadOnlyList<(DiffKind Kind, string Text)> rows)
        {
            var marks = new IReadOnlyList<DiffSpan>[rows.Count];
            for (int index = 0; index < rows.Count; index++) marks[index] = None;

            int cursor = 0;
            while (cursor < rows.Count)
            {
                if (rows[cursor].Kind != DiffKind.Removed)
                {
                    cursor++;
                    continue;
                }

                int removedStart = cursor;
                while (cursor < rows.Count && rows[cursor].Kind == DiffKind.Removed) cursor++;
                int addedStart = cursor;
                while (cursor < rows.Count && rows[cursor].Kind == DiffKind.Added) cursor++;

                int pairs = Math.Min(addedStart - removedStart, cursor - addedStart);
                for (int offset = 0; offset < pairs; offset++)
                {
                    int oldRow = removedStart + offset, newRow = addedStart + offset;
                    if (Compare(rows[oldRow].Text, rows[newRow].Text) is var (oldSpans, newSpans))
                    {
                        marks[oldRow] = oldSpans;
                        marks[newRow] = newSpans;
                    }
                }
            }

            return marks;
        }

        /// <summary>
        /// The changed ranges of each side, or null when the pair should not be marked:
        /// identical, over the length bound, or sharing too little to read as an edit.
        /// </summary>
        public static (IReadOnlyList<DiffSpan> Old, IReadOnlyList<DiffSpan> New)? Compare(string before, string after)
        {
            if (before.Length > MaxLineChars || after.Length > MaxLineChars
                || string.Equals(before, after, StringComparison.Ordinal))
            {
                return null;
            }

            var oldTokens = Tokenize(before);
            var newTokens = Tokenize(after);
            int m = oldTokens.Count, n = newTokens.Count;
            var lcs = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
            {
                for (int j = n - 1; j >= 0; j--)
                {
                    lcs[i, j] = Same(before, oldTokens[i], after, newTokens[j])
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var oldKept = new bool[m];
            var newKept = new bool[n];
            for (int i = 0, j = 0; i < m && j < n;)
            {
                if (Same(before, oldTokens[i], after, newTokens[j]))
                {
                    oldKept[i++] = true;
                    newKept[j++] = true;
                }
                else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
                else j++;
            }

            // Whitespace alone keeps two unrelated lines looking alike; weigh visible text.
            int visible = Math.Max(Visible(before), Visible(after));
            int sharedVisible = 0;
            for (int i = 0; i < m; i++)
            {
                if (oldKept[i]) sharedVisible += Visible(before.AsSpan(oldTokens[i].Start, oldTokens[i].Length));
            }

            if (visible == 0 || (double)sharedVisible / visible < MinSharedRatio) return null;

            return (Ranges(before, oldTokens, oldKept), Ranges(after, newTokens, newKept));
        }

        /// <summary>Words, runs of whitespace, and single punctuation marks.</summary>
        private static List<DiffSpan> Tokenize(string text)
        {
            var tokens = new List<DiffSpan>();
            int index = 0;
            while (index < text.Length)
            {
                int start = index;
                char first = text[index];
                if (IsWord(first))
                {
                    while (index < text.Length && IsWord(text[index])) index++;
                }
                else if (char.IsWhiteSpace(first))
                {
                    while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                }
                else
                {
                    index++;
                }

                tokens.Add(new DiffSpan(start, index - start));
            }

            return tokens;
        }

        private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';

        private static bool Same(string left, DiffSpan a, string right, DiffSpan b) =>
            a.Length == b.Length
            && left.AsSpan(a.Start, a.Length).SequenceEqual(right.AsSpan(b.Start, b.Length));

        private static int Visible(ReadOnlySpan<char> text)
        {
            int count = 0;
            foreach (char character in text)
            {
                if (!char.IsWhiteSpace(character)) count++;
            }

            return count;
        }

        /// <summary>
        /// Joins changed tokens into ranges. A whitespace run between two changes joins
        /// them, so "a b" → "x y" reads as one change rather than two islands.
        /// </summary>
        private static IReadOnlyList<DiffSpan> Ranges(string text, List<DiffSpan> tokens, bool[] kept)
        {
            var ranges = new List<DiffSpan>();
            int start = -1, end = -1;
            for (int index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                bool changed = !kept[index];
                bool bridge = kept[index] && start >= 0
                    && Visible(text.AsSpan(token.Start, token.Length)) == 0
                    && index + 1 < tokens.Count && !kept[index + 1];
                if (changed || bridge)
                {
                    if (start < 0) start = token.Start;
                    end = token.Start + token.Length;
                }
                else if (start >= 0)
                {
                    ranges.Add(new DiffSpan(start, end - start));
                    start = -1;
                }
            }

            if (start >= 0) ranges.Add(new DiffSpan(start, end - start));
            return ranges;
        }
    }
}
