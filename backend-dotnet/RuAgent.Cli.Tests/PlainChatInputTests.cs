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

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void RedirectedStdinDoesNotStartTerminalGui()
    {
        Assert.Equal(ChatPresentationMode.Plain, ChatPresentationSelection.Resolve(
            false, false, 120, 40, null, "xterm-256color", false, inputRedirected: true));
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
