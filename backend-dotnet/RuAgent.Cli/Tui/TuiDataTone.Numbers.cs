using System.Globalization;
using System.Text.RegularExpressions;

namespace RuAgent.Cli.Tui;

internal static partial class TuiDataTone
{
    private const string Sign = @"[+\-−]?";
    private const string Space = @"[ \t\u00a0]*";
    private const string Currency = @"(?:[$€£¥₫]|(?i:USD|EUR|GBP|JPY|CNY|VND|đ))";
    private const string FractionOrGrouped = @"(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d{1,3}(?:\.\d{3})+(?:,\d+)?|\d+[.,]\d+)";
    private const string Number = @"(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d{1,3}(?:\.\d{3})+(?:,\d+)?|\d+(?:[.,]\d+)?)";

    private static readonly Regex NumericPattern = new(
        @"(?<![\w.,])(?:" + Currency + Space + Sign + Number
        + "|" + Sign + Currency + Space + Number
        + "|" + Sign + Number + Space + "(?:%|" + Currency + ")"
        + "|" + Sign + FractionOrGrouped + @")(?!\w|[.,]\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DatePattern = new(
        @"(?<![\w./-])(?:\d{4}-\d{2}-\d{2}|\d{1,2}(?<separator>[/.-])\d{1,2}\k<separator>\d{4})(?!\w|[./-]\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy"];

    private static void AddNumericMatches(string text, List<Match> matches)
    {
        // Filenames win over numeric substrings. Reserve entire date candidates,
        // including invalid dates, so they never fall back to colored fragments.
        var files = matches.ToArray();
        var reserved = files.Select(match => (match.Start, match.End)).ToList();
        foreach (System.Text.RegularExpressions.Match date in DatePattern.Matches(text))
        {
            var start = date.Index;
            var end = start + date.Length;
            reserved.Add((start, end));
            if (!files.Any(file => file.Start < end && file.End > start)
                && DateOnly.TryParseExact(date.Value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                matches.Add(new(start, end, TuiToken.Brand));
        }
        reserved.Sort((left, right) => left.Start.CompareTo(right.Start));
        var blocked = 0;
        foreach (System.Text.RegularExpressions.Match number in NumericPattern.Matches(text))
        {
            var start = number.Index;
            var end = start + number.Length;
            while (blocked < reserved.Count && reserved[blocked].End <= start) blocked++;
            if (blocked < reserved.Count && reserved[blocked].Start < end) continue;
            matches.Add(new(start, end, TuiToken.Brand));
        }
    }
}
