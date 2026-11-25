using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Inside a changed line the diff marks only the words that changed, and stays
/// silent when the pair is too different, too long or has no partner.
/// </summary>
public sealed class IntraLineDiffTests
{
    private static string Marked(string text, IReadOnlyList<DiffSpan> spans) =>
        string.Join("|", spans.Select(span => text.Substring(span.Start, span.Length)));

    [Fact]
    public void A_changed_number_is_the_only_mark()
    {
        const string before = "timeout = 30   # seconds, for API requests";
        const string after = "timeout = 60   # seconds, for API requests";

        var (old, @new) = IntraLineDiff.Compare(before, after)!.Value;

        Assert.Equal("30", Marked(before, old));
        Assert.Equal("60", Marked(after, @new));
    }

    [Fact]
    public void An_added_argument_marks_only_the_new_side()
    {
        const string before = "Run(path, token);";
        const string after = "Run(path, options, token);";

        var (old, @new) = IntraLineDiff.Compare(before, after)!.Value;

        Assert.Empty(old);
        Assert.Equal("options,", Marked(after, @new).Replace(" ", string.Empty));
    }

    [Fact]
    public void Neighbouring_changed_words_read_as_one_range()
    {
        const string before = "var total = first second + rest;";
        const string after = "var total = alpha beta + rest;";

        var (old, @new) = IntraLineDiff.Compare(before, after)!.Value;

        Assert.Equal("first second", Marked(before, old));
        Assert.Equal("alpha beta", Marked(after, @new));
    }

    [Theory]
    [InlineData("return a + b;", "Console.WriteLine(message);")]
    [InlineData("same", "same")]
    public void Unrelated_or_identical_lines_get_no_marks(string before, string after) =>
        Assert.Null(IntraLineDiff.Compare(before, after));

    [Fact]
    public void Overlong_lines_keep_only_their_whole_line_tint()
    {
        string before = new string('a', IntraLineDiff.MaxLineChars) + " x";
        string after = new string('a', IntraLineDiff.MaxLineChars) + " y";

        Assert.Null(IntraLineDiff.Compare(before, after));
    }

    [Fact]
    public void Removed_and_added_runs_pair_line_by_line()
    {
        var rows = new (DiffKind, string)[]
        {
            (DiffKind.Unchanged, "int a = 1;"),
            (DiffKind.Removed, "int b = 2;"),
            (DiffKind.Removed, "int c = 3;"),
            (DiffKind.Added, "int b = 20;"),
            (DiffKind.Added, "int c = 30;"),
            (DiffKind.Added, "int d = 4;"),
        };

        var marks = IntraLineDiff.Mark(rows);

        Assert.Empty(marks[0]);
        Assert.Equal("2", Marked(rows[1].Item2, marks[1]));
        Assert.Equal("3", Marked(rows[2].Item2, marks[2]));
        Assert.Equal("20", Marked(rows[3].Item2, marks[3]));
        Assert.Equal("30", Marked(rows[4].Item2, marks[4]));
        // A line with no partner is wholly new; its whole-line tint already says so.
        Assert.Empty(marks[5]);
    }

    [Fact]
    public void An_added_run_without_removed_lines_has_no_marks()
    {
        var rows = new (DiffKind, string)[] { (DiffKind.Added, "x = 1"), (DiffKind.Added, "y = 2") };

        Assert.All(IntraLineDiff.Mark(rows), Assert.Empty);
    }
}
