using RuAgent.Cli.Attachments;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatPromptEchoStoredTests
{
    [Fact]
    public void Replayed_attachment_prompt_echoes_like_the_live_turn()
    {
        // A reopened session used to echo the inlined "[Attached file: ...]" envelope and file text.
        var document = new CliTextAttachment("notes.md", "# plan\nstep one", 15);
        var stored = AttachedPrompt.Compose("summarize this", [document]);

        var echoed = ChatPromptEcho.FormatStored(stored);

        Assert.Equal(ChatPromptEcho.Format("summarize this", ["[file 1: notes.md]"]), echoed);
        Assert.DoesNotContain("Attached file", echoed, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_prompt_echoes_unchanged()
    {
        Assert.Equal(ChatPromptEcho.Format("hello"), ChatPromptEcho.FormatStored("hello"));
    }

    [Fact]
    public void Split_stored_returns_the_typed_text_and_one_label_per_document()
    {
        // The terminal's history replay echoes the typed text through the same user block
        // the composer produced live, so the envelope must be stripped here, not there.
        var first = new CliTextAttachment("notes.md", "# plan", 6);
        var second = new CliTextAttachment("spec.md", "body", 4);
        var stored = AttachedPrompt.Compose("summarize this", [first, second]);

        var (text, labels) = ChatPromptEcho.SplitStored(stored);

        Assert.Equal("summarize this", text);
        Assert.Equal(["[file 1: notes.md]", "[file 2: spec.md]"], labels);
    }

    [Fact]
    public void Split_stored_returns_an_ordinary_prompt_without_labels()
    {
        var (text, labels) = ChatPromptEcho.SplitStored("hello");

        Assert.Equal("hello", text);
        Assert.Empty(labels);
    }
}
