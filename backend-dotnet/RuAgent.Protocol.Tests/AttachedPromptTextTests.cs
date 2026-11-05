using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class AttachedPromptTextTests
{
    private const string Code = "#include <iostream>\nint main() {\n    return 0;\n}";

    [Fact]
    public void Gui_envelope_splits_into_the_file_and_the_typed_text()
    {
        // The GUI composer writes AppendLine (CRLF on Windows) and labels "632 B".
        var prompt = $"[Attached file: Untitled1.cpp (51 B)]\r\n```cpp\r\n{Code.Replace("\n", "\r\n")}\r\n```\r\n\r\nem đọc được file này không?";

        var parts = Assert.IsType<AttachedPromptParts>(AttachedPromptText.TrySplit(prompt));

        var document = Assert.Single(parts.Documents);
        Assert.Equal("Untitled1.cpp", document.FileName);
        Assert.Equal("51 B", document.SizeLabel);
        Assert.Equal("cpp", document.Language);
        Assert.Equal(Code, document.Content);
        Assert.Equal("em đọc được file này không?", parts.Text);
    }

    [Fact]
    public void Cli_envelope_with_several_files_and_no_typed_text_splits()
    {
        var prompt = "[Attached file: a.md (5B)]\n```md\n# hi\n```\n\n[Attached file: b.json (2B)]\n```json\n{}\n```";

        var parts = Assert.IsType<AttachedPromptParts>(AttachedPromptText.TrySplit(prompt));

        Assert.Equal(["a.md", "b.json"], parts.Documents.Select(document => document.FileName));
        Assert.Equal("{}", parts.Documents[1].Content);
        Assert.Equal(string.Empty, parts.Text);
        Assert.Equal("a.md", AttachedPromptText.SummaryText(prompt));
    }

    [Fact]
    public void A_fence_inside_the_file_is_kept_by_the_recorded_size()
    {
        const string markdown = "# plan\n```\n\nstill the file";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(markdown);
        var prompt = $"[Attached file: plan.md ({bytes} B)]\n```md\n{markdown}\n```\n\nreview it";

        var parts = Assert.IsType<AttachedPromptParts>(AttachedPromptText.TrySplit(prompt));

        Assert.Equal(markdown, Assert.Single(parts.Documents).Content);
        Assert.Equal("review it", parts.Text);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("see [Attached file: x.cs (1 B)] below")]
    [InlineData("[Attached file: x.cs (1 B)]\nno fence")]
    [InlineData("[Attached file: x.cs (1 B)]\n```cs\nunterminated")]
    public void Anything_but_a_well_formed_leading_envelope_stays_plain_text(string prompt)
    {
        Assert.Null(AttachedPromptText.TrySplit(prompt));
        Assert.Equal(prompt, AttachedPromptText.SummaryText(prompt));
    }

    [Fact]
    public void Summary_prefers_the_typed_text()
    {
        Assert.Equal("fix it", AttachedPromptText.SummaryText("[Attached file: a.py (1 B)]\n```py\nx\n```\n\nfix it"));
    }
}
