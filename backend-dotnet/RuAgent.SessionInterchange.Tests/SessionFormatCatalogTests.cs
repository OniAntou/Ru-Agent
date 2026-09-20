using RuAgent.SessionInterchange;
using Xunit;

namespace RuAgent.SessionInterchange.Tests;

public sealed class SessionFormatCatalogTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Names_the_formats_it_knows_when_asked_for_one_it_does_not()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => SessionFormatCatalog.Require("cursor"));

        Assert.Contains("claude-code", error.Message, StringComparison.Ordinal);
        Assert.Contains("codex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Finds_a_format_regardless_of_how_the_id_was_typed()
    {
        Assert.Same(SessionFormatCatalog.All[0], SessionFormatCatalog.Find("Claude-Code"));
        Assert.Null(SessionFormatCatalog.Find("cursor"));
    }

    [Fact]
    public void A_session_written_for_one_agent_reads_back_from_the_other()
    {
        // The whole point of the seam: a conversation goes out in one agent's shape and
        // comes back as the same conversation, not as a file only its author can read.
        SessionTranscript source = new(
            "11111111-1111-7111-8111-111111111111",
            "Bring a session across",
            "D:\\LLM",
            Stamp,
            [
                new TranscriptEntry(TranscriptEntryKind.UserMessage, Stamp.AddSeconds(1), Text: "do the thing"),
                new TranscriptEntry(TranscriptEntryKind.Reasoning, Stamp.AddSeconds(2), Text: "thinking about it"),
                new TranscriptEntry(
                    TranscriptEntryKind.ToolCall,
                    Stamp.AddSeconds(3),
                    ToolName: "read",
                    ToolCallId: "call_1",
                    ToolArguments: "{\"path\":\"Program.cs\"}"),
                new TranscriptEntry(TranscriptEntryKind.ToolResult, Stamp.AddSeconds(4), ToolCallId: "call_1", ToolResult: "file contents"),
                new TranscriptEntry(TranscriptEntryKind.AssistantMessage, Stamp.AddSeconds(5), Text: "done"),
            ]);

        SessionTranscript throughClaude = SessionFormatCatalog.Require("claude-code")
            .Read(SessionFormatCatalog.Require("claude-code").Write(source)[0].Content);
        SessionTranscript throughCodex = SessionFormatCatalog.Require("codex")
            .Read(SessionFormatCatalog.Require("codex").Write(throughClaude)[0].Content);

        Assert.Equal(
            source.Entries.Select(entry => (entry.Kind, entry.Text)),
            throughCodex.Entries.Select(entry => (entry.Kind, entry.Text)));
        Assert.Equal("D:\\LLM", throughCodex.WorkingDirectory);

        // Two turns, not five entries: a reader counting sessions counts what was said,
        // not the thinking and the tool traffic that surrounded it.
        Assert.Equal(2, throughCodex.MessageCount);

        // The title does not survive the trip, and cannot: Claude Code has no field for
        // one, and Codex keeps its copy in the sibling index file rather than in the
        // rollout. A reader therefore names the session after the first thing that was
        // asked, which is what Claude Code's own picker does too.
        Assert.Equal("do the thing", throughCodex.Title);
    }
}
