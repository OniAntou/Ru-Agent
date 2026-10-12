using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiMarkdownStreamingRegressionTests
{
    [Theory]
    [InlineData("```csharp\nvar x = 1;\n```\nAfter.\n")]
    [InlineData("# Heading\n- bullet **bold** and `code`\n> quote\n")]
    [InlineData("before\n---\nafter\n")]
    [InlineData("a **bold** word without a final newline")]
    [InlineData("hello\n<tool_call>call\n</tool_call>after\n")]
    public void EveryTwoChunkPartitionMatchesWholeReply(string reply)
    {
        var expected = Render([reply]);
        for (int split = 1; split < reply.Length; split++)
            Assert.Equal(expected, Render([reply[..split], reply[split..]]));
        Assert.Equal(expected, Render(reply.Select(c => c.ToString())));
    }

    [Fact]
    public void LongCodeTailSurvivesResize()
    {
        using var colors = TuiPalette.PinLevel(TuiColorLevel.None);
        var surface = new TuiSurface(new StringWriter()) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 40 };
        transcript.AppendAssistant("```csharp\nvar s = \"" + new string('x', 90) + "TAIL_SENTINEL\";\n```\n");
        transcript.Flush();
        transcript.Width = 200;
        Assert.Contains(transcript.RepaintPreview(), row => row.Contains("TAIL_SENTINEL"));
    }

    private static string Render(IEnumerable<string> chunks)
    {
        using var colors = TuiPalette.PinLevel(TuiColorLevel.None);
        var output = new StringWriter();
        var surface = new TuiSurface(output) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 80 };
        foreach (var chunk in chunks) transcript.AppendAssistant(chunk);
        transcript.Flush();
        return output.ToString();
    }
}
