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
}
