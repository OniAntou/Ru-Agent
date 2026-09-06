using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class InteractiveLineReaderTests
{
    [Fact]
    public void AutoComplete_ReturnsExactMatch_WhenPrefixMatchesSingleCandidate()
    {
        var candidates = new[] { "/help", "/model", "/backend", "/status" };

        var result1 = InteractiveLineReader.AutoComplete("/he", candidates);
        Assert.Equal("/help", result1);

        var result2 = InteractiveLineReader.AutoComplete("/mo", candidates);
        Assert.Equal("/model", result2);

        var result3 = InteractiveLineReader.AutoComplete("/B", candidates);
        Assert.Equal("/backend", result3);
    }

    [Fact]
    public void AutoComplete_ReturnsNull_WhenAmbiguousOrNoMatch()
    {
        var candidates = new[] { "/fork", "/format" };

        // Ambiguous prefix
        var ambiguous = InteractiveLineReader.AutoComplete("/f", candidates);
        Assert.Null(ambiguous);

        // No match
        var noMatch = InteractiveLineReader.AutoComplete("/unknown", candidates);
        Assert.Null(noMatch);

        // Not a slash command
        var normalText = InteractiveLineReader.AutoComplete("hello", candidates);
        Assert.Null(normalText);
    }

    [Fact]
    public void History_AppendsAndDedupesConsecutiveItems()
    {
        var reader = new InteractiveLineReader();
        reader.AddToHistory("first command");
        reader.AddToHistory("first command"); // consecutive duplicate
        reader.AddToHistory("second command");
        reader.AddToHistory("   "); // whitespace ignored
        reader.AddToHistory("first command"); // non-consecutive allowed

        Assert.Equal(3, reader.History.Count);
        Assert.Equal("first command", reader.History[0]);
        Assert.Equal("second command", reader.History[1]);
        Assert.Equal("first command", reader.History[2]);
    }

    [Fact]
    public async Task ReadLineWithContinuationAsync_CombinesLinesWithBackslash()
    {
        var reader = new InteractiveLineReader();
        var input = "line 1 \\\nline 2 \\\nline 3\n";
        using var inReader = new StringReader(input);
        using var outWriter = new StringWriter();

        var result = await reader.ReadLineWithContinuationAsync(
            inReader,
            outWriter,
            "prompt > ",
            "... > ");

        Assert.NotNull(result);
        Assert.Contains("line 1", result);
        Assert.Contains("line 2", result);
        Assert.Contains("line 3", result);
        Assert.Contains(result, reader.History);
    }

    [Fact]
    public async Task ReadSingleLineAsync_FallsBackGracefully_OnStringReader()
    {
        var reader = new InteractiveLineReader();
        using var inReader = new StringReader("single command line\n");
        using var outWriter = new StringWriter();

        var result = await reader.ReadSingleLineAsync(inReader, outWriter, "prompt > ");

        Assert.Equal("single command line", result);
        Assert.Equal("prompt > ", outWriter.ToString());
    }
}
