using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatInputContractTests
{
    [Fact]
    public void AutoComplete_ReturnsExactMatch_WhenPrefixMatchesSingleCandidate()
    {
        var candidates = new[] { "/help", "/model", "/backend", "/status" };

        var result1 = ChatCommandCatalog.AutoComplete("/he", candidates);
        Assert.Equal("/help", result1);

        var result2 = ChatCommandCatalog.AutoComplete("/mo", candidates);
        Assert.Equal("/model", result2);

        var result3 = ChatCommandCatalog.AutoComplete("/B", candidates);
        Assert.Equal("/backend", result3);
    }

    [Fact]
    public void AutoComplete_ReturnsNull_WhenAmbiguousOrNoMatch()
    {
        var candidates = new[] { "/format", "/filter" };

        // Ambiguous prefix
        var ambiguous = ChatCommandCatalog.AutoComplete("/f", candidates);
        Assert.Null(ambiguous);

        // No match
        var noMatch = ChatCommandCatalog.AutoComplete("/unknown", candidates);
        Assert.Null(noMatch);

        // Not a slash command
        var normalText = ChatCommandCatalog.AutoComplete("hello", candidates);
        Assert.Null(normalText);
    }

    [Fact]
    public void History_AppendsAndDedupesConsecutiveItems()
    {
        var reader = new PlainChatInput();
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

    [Theory]
    [InlineData(null, 4096, "")]
    [InlineData("", 4096, "")]
    [InlineData("sk-test-key", 4096, "sk-test-key")]
    [InlineData("  sk-test-key  \r\n", 4096, "sk-test-key")]
    [InlineData("sk-test-key\r\nsecond-line", 4096, "sk-test-key")]
    [InlineData("sk-test-key", 0, "")]
    [InlineData("sk-test-key", 6, "sk-tes")]
    public void SanitizePastedText_KeepsFirstTrimmedLineCapped(string? text, int maxChars, string expected)
    {
        Assert.Equal(expected, ChatCommandCatalog.SanitizePastedText(text, maxChars));
    }

    [Fact]
    public async Task ReadLineWithContinuationAsync_CombinesLinesWithBackslash()
    {
        var reader = new PlainChatInput();
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
        var reader = new PlainChatInput();
        using var inReader = new StringReader("single command line\n");
        using var outWriter = new StringWriter();

        var result = await reader.ReadSingleLineAsync(inReader, outWriter, "prompt > ");

        Assert.Equal("single command line", result);
        Assert.Equal("prompt > ", outWriter.ToString());
    }

    [Fact]
    public void FindMatches_ReturnsAllSlashCommands_WhenInputIsSingleSlash()
    {
        var matches = ChatCommandCatalog.FindMatches("/");
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.Command == "/model");
        Assert.Contains(matches, m => m.Command == "/help");
        Assert.Contains(matches, m => m.Command == "/exit");
    }

    [Fact]
    public void FindMatches_FiltersPrefix_CaseInsensitively()
    {
        var matches = ChatCommandCatalog.FindMatches("/m");
        Assert.Contains(matches, m => m.Command == "/model");
        Assert.DoesNotContain(matches, m => m.Command == "/help");

        var matchesUpper = ChatCommandCatalog.FindMatches("/MO");
        Assert.Contains(matchesUpper, m => m.Command == "/model");

        var emptyMatches = ChatCommandCatalog.FindMatches("/nonexistent");
        Assert.Empty(emptyMatches);
    }


}
