using System.Text.RegularExpressions;

namespace RuAgent.Cli.Tui;

/// <summary>Shared deterministic accents; explicit Markdown styling always wins.</summary>
internal static partial class TuiDataTone
{
    private static readonly Regex FilePathPattern = new(
        @"\b[A-Za-z0-9][\w.-]{0,63}\.(?i:md|json|jsonl|cs|py|ps1|cmd|bat|txt|yml|yaml|toml|ini|log|js|jsx|ts|tsx|html|css)\b", RegexOptions.Compiled);

    internal readonly record struct Match(int Start, int End, TuiToken Token);

    internal static IReadOnlyList<Match> FindMatches(string text)
    {
        var matches = new List<Match>();
        foreach (System.Text.RegularExpressions.Match match in FilePathPattern.Matches(text))
            matches.Add(new(match.Index, match.Index + match.Length, TuiToken.Brand2));
        AddNumericMatches(text, matches);
        matches.Sort((left, right) => left.Start.CompareTo(right.Start));
        return matches;
    }

    internal static IReadOnlyList<TranscriptRun> Decorate(IReadOnlyList<TranscriptRun> runs)
    {
        var text = string.Concat(runs.Select(run => run.Text));
        var matches = FindMatches(text);
        if (matches.Count == 0) return runs;
        var output = new TranscriptRunBuilder();
        var offset = 0;
        foreach (var run in runs)
        {
            var end = offset + run.Text.Length;
            if (run.Token != TuiToken.Ink || run.Bold || run.Dim)
                output.Append(run.Text, run.Token, run.Bold, run.Dim);
            else
            {
                var position = offset;
                foreach (var match in matches)
                {
                    var low = Math.Max(match.Start, offset);
                    var high = Math.Min(match.End, end);
                    if (high <= low) continue;
                    if (low > position) output.Append(text[position..low]);
                    output.Append(text[low..high], match.Token);
                    position = high;
                }
                if (position < end) output.Append(text[position..end]);
            }
            offset = end;
        }
        return output.Line().Runs;
    }
}
