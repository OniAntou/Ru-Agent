using RuAgent.Cli.Attachments;
using Xunit;

namespace RuAgent.Cli.Tests.Attachments;

public sealed class AttachedPromptTests
{
    [Fact]
    public void Compose_WithoutAttachmentsReturnsTheText()
    {
        Assert.Equal("hello", AttachedPrompt.Compose("hello", []));
    }

    [Fact]
    public void Compose_InlinesFencedBlocksAheadOfTheText()
    {
        var attachments = new List<CliTextAttachment>
        {
            new("notes.md", "# Hi", 2048),
            new("report.docx", "Quarterly report", 4096),
        };

        var prompt = AttachedPrompt.Compose("summarize", attachments);

        Assert.Contains("[Attached file: notes.md (2KB)]", prompt, StringComparison.Ordinal);
        Assert.Contains("```md", prompt, StringComparison.Ordinal);
        Assert.Contains("# Hi", prompt, StringComparison.Ordinal);
        Assert.Contains("[Attached file: report.docx (4KB)]", prompt, StringComparison.Ordinal);
        Assert.Contains("```docx", prompt, StringComparison.Ordinal);
        Assert.EndsWith("summarize", prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf("notes.md", StringComparison.Ordinal) < prompt.IndexOf("summarize", StringComparison.Ordinal),
            "attachments must precede the typed text");
    }

    [Fact]
    public void Compose_EmptyTextStillCarriesTheAttachments()
    {
        var prompt = AttachedPrompt.Compose(string.Empty, [new CliTextAttachment("a.txt", "body", 4)]);

        Assert.Contains("body", prompt, StringComparison.Ordinal);
        Assert.EndsWith("```", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Label_ShowsNameAndSize()
    {
        var attachment = new CliTextAttachment("notes.md", "x", 120 * 1024);

        Assert.Equal("notes.md  120KB", attachment.Label);
    }
}
