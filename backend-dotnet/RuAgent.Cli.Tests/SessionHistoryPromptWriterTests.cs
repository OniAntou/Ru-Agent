using RuAgent.Cli.Attachments;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SessionHistoryPromptWriterTests
{
    [Fact]
    public void Terminal_mode_echoes_the_typed_text_as_the_composer_block()
    {
        var echoed = new List<string>();
        var lines = new List<string>();

        SessionHistoryPromptWriter.Write("hello", tgMode: true, echoed.Add, lines.Add);

        Assert.Equal(["hello"], echoed);
        Assert.Empty(lines);
    }

    [Fact]
    public void Text_mode_keeps_the_plain_echo()
    {
        var echoed = new List<string>();
        var lines = new List<string>();

        SessionHistoryPromptWriter.Write("hello", tgMode: false, echoed.Add, lines.Add);

        Assert.Empty(echoed);
        Assert.Equal(["> hello"], lines);
    }

    [Fact]
    public void Inlined_documents_become_labels_beside_the_echoed_text()
    {
        var document = new CliTextAttachment("notes.md", "# plan", 6);
        var stored = AttachedPrompt.Compose("summarize this", [document]);
        var echoed = new List<string>();
        var lines = new List<string>();

        SessionHistoryPromptWriter.Write(stored, tgMode: true, echoed.Add, lines.Add);

        Assert.Equal(["summarize this"], echoed);
        Assert.Equal(["  [file 1: notes.md]"], lines);
    }

    [Fact]
    public void Text_mode_replays_the_labels_in_the_plain_echo()
    {
        var document = new CliTextAttachment("notes.md", "# plan", 6);
        var stored = AttachedPrompt.Compose("summarize this", [document]);
        var lines = new List<string>();

        SessionHistoryPromptWriter.Write(stored, tgMode: false, _ => { }, lines.Add);

        var line = Assert.Single(lines);
        Assert.Contains("> summarize this", line, StringComparison.Ordinal);
        Assert.Contains("[file 1: notes.md]", line, StringComparison.Ordinal);
    }
}
