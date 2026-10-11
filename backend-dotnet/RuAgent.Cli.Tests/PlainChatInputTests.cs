using RuAgent.Cli.Tui;
using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class PlainChatInputTests
{
    [Fact]
    public async Task ReadsLinesWithoutCursorControlsEvenWhenColourIsEnabled()
    {
        using var colour = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        var reader = new PlainChatInput();
        using var input = new StringReader("hello\n/exit\n");
        using var output = new StringWriter();
        Assert.Equal("hello", await reader.ReadLineWithContinuationAsync(input, output, "> ", "... "));
        Assert.Equal("/exit", await reader.ReadLineWithContinuationAsync(input, output, "> ", "... "));
        Assert.Equal("> > ", output.ToString());
        Assert.Equal(new[] { "hello", "/exit" }, reader.History);
    }

    [Fact]
    public async Task ContinuationAndPartialEofPreserveSubmittedText()
    {
        var reader = new PlainChatInput();
        using var input = new StringReader("one " + "\\" + "\n");
        using var output = new StringWriter();
        Assert.Equal("one " + Environment.NewLine, await reader.ReadLineWithContinuationAsync(input, output, "> ", "... "));
        Assert.Null(await reader.ReadLineWithContinuationAsync(input, output, "> ", "... "));
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeReading()
    {
        var reader = new PlainChatInput();
        using var input = new StringReader("hello\n");
        using var output = new StringWriter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadLineWithContinuationAsync(input, output, "> ", "... ", cancelled.Token));
        Assert.Empty(reader.History);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void HistoryIsBoundedAndOnlyDeduplicatesConsecutiveSubmissions()
    {
        var reader = new PlainChatInput();
        for (var i = 0; i < 120; i++) reader.AddToHistory($"prompt {i}");
        reader.AddToHistory("prompt 119");
        reader.AddToHistory(" ");
        Assert.Equal(100, reader.History.Count);
        Assert.Equal("prompt 20", reader.History[0]);
        Assert.Equal("prompt 119", reader.History[^1]);
    }

    [Fact]
    public void RedirectedStdinDoesNotStartTerminalGui()
    {
        Assert.Equal(TuiEngine.Sequential, TuiEngineSelection.Resolve(
            false, false, 120, 40, null, "xterm-256color", false, inputRedirected: true));
    }
    [Fact]
    public async Task LoopPreservesQuotesAndStopsBeforeTheNextSubmission()
    {
        var reader = new PlainChatInput();
        using var input = new StringReader("\"quoted prompt\"\n/exit\nnot submitted\n");
        using var output = new StringWriter();
        var submitted = new List<string>();
        var exit = await reader.RunAsync(input, output, (line, _) =>
        {
            submitted.Add(line);
            return Task.FromResult<int?>(line == "/exit" ? 7 : null);
        });
        Assert.Equal(7, exit);
        Assert.Equal(new[] { "\"quoted prompt\"", "/exit" }, submitted);
        Assert.Equal("not submitted", await input.ReadLineAsync());
    }

    [Fact]
    public async Task EmptyInputDoesNotSubmitOrAskForMoreInput()
    {
        using var input = new StringReader("");
        using var output = new StringWriter();
        var exit = await new PlainChatInput().RunAsync(input, output,
            (_, _) => throw new InvalidOperationException("must not submit at EOF"));
        Assert.Equal(0, exit);
    }

    [Fact]
    public void EchoPreservesLinesAndAttachmentsWithoutTerminalControls()
    {
        var echo = ChatPromptEcho.Format("\"first\"\r\n\u001b[2Jsecond", ["[text: notes.txt]"]);
        Assert.Equal("> \"first\"" + Environment.NewLine + "> second" + Environment.NewLine + "  [text: notes.txt]", echo);
        Assert.DoesNotContain("\u001b", echo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretInputNeverReadsOrWritesCredentialsThroughAnInjectedWriter()
    {
        using var output = new StringWriter();
        Assert.Null(await ConsoleSecretInput.ReadAsync(output, "API key: "));
        Assert.Empty(output.ToString());
    }

}
