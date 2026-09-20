using RuAgent.SessionInterchange;
using Xunit;

namespace RuAgent.SessionInterchange.Tests;

public sealed class AntigravitySessionFormatTests
{
    private readonly AntigravitySessionFormat _format = new();

    /// <summary>
    /// The steps below are the shape a real transcript on this machine has, including the
    /// envelope Antigravity wraps a user's words in and a model turn that carries both
    /// thinking and a tool call.
    /// </summary>
    private const string RealTranscript = """
        {"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-09-04T16:01:50Z","content":"<USER_REQUEST>\nhãy đọc codebase và review\n</USER_REQUEST>"}
        {"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-09-04T16:01:51Z","thinking":"**Bắt đầu đọc và xem xét codebase.**","tool_calls":[{"name":"list_dir","args":{"DirectoryPath":"\"d:\\\\LLM\"","toolSummary":"\"List root directory\""}}]}
        {"step_index":2,"source":"SYSTEM","type":"GENERIC","status":"DONE","created_at":"2026-09-04T16:01:52Z","content":"File listing: 24 directories"}
        {"step_index":3,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-09-04T16:01:53Z","content":"Đã đọc xong codebase."}
        """;

    [Fact]
    public void Reads_the_steps_a_real_transcript_has()
    {
        SessionTranscript transcript = _format.Read(RealTranscript);

        // The user's words, not Antigravity's envelope around them.
        Assert.Equal("hãy đọc codebase và review", transcript.Title);
        Assert.Equal(
            [
                TranscriptEntryKind.UserMessage,
                TranscriptEntryKind.Reasoning,
                TranscriptEntryKind.ToolCall,
                TranscriptEntryKind.ToolResult,
                TranscriptEntryKind.AssistantMessage,
            ],
            transcript.Entries.Select(entry => entry.Kind));

        var call = transcript.Entries[2];
        Assert.Equal("list_dir", call.ToolName);
        Assert.Contains("List root directory", call.ToolArguments, StringComparison.Ordinal);

        // A call in this log has no id, so none is invented for it.
        Assert.Null(call.ToolCallId);
    }

    [Fact]
    public void Refuses_a_payload_that_is_not_one_of_its_steps()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => _format.Read("{\"hello\":\"world\"}"));

        Assert.Contains("Antigravity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_transcript_whose_steps_carry_nothing_to_read()
    {
        string empty = """{"step_index":0,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-09-04T16:01:50Z"}""";

        Assert.Throws<InvalidDataException>(() => _format.Read(empty));
    }

    /// <summary>
    /// The refusal names the missing evidence rather than the missing feature: a reader who
    /// is told "protobuf payloads, schema not on disk" knows why and what would change it.
    /// </summary>
    [Fact]
    public void Cannot_be_written_and_says_exactly_why()
    {
        Assert.False(_format.CanWrite);
        Assert.Contains("protobuf", _format.WriteRefusal, StringComparison.Ordinal);

        var transcript = new SessionTranscript(
            "11111111-1111-7111-8111-111111111111",
            "title",
            @"D:\LLM",
            DateTimeOffset.UnixEpoch,
            [new TranscriptEntry(TranscriptEntryKind.UserMessage, DateTimeOffset.UnixEpoch, Text: "hi")]);

        Assert.Throws<NotSupportedException>(() => _format.Write(transcript));
    }
}
