using System.Text.Json.Nodes;
using RuAgent.SessionInterchange;
using Xunit;

namespace RuAgent.SessionInterchange.Tests;

public sealed class ClaudeCodeSessionFormatTests
{
    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 7, 12, 10, 50, 23, TimeSpan.Zero).AddMilliseconds(370);

    private readonly ClaudeCodeSessionFormat _format = new();

    /// <summary>
    /// The lines below are the shape a real transcript on this machine has, including
    /// the queue plumbing and the attachment deltas that are not conversation.
    /// </summary>
    private const string RealTranscript = """
        {"type":"queue-operation","operation":"enqueue","timestamp":"2026-07-12T10:50:23.359Z","sessionId":"75127d8e-65c4-4981-88ba-3048fbf0d494"}
        {"parentUuid":null,"isSidechain":false,"promptId":"b04d4297-12da-4587-82b4-2632903b5711","type":"user","message":{"role":"user","content":[{"type":"text","text":"Hello"}]},"uuid":"aadd8702-3f42-4107-8ac7-645dfa277629","timestamp":"2026-07-12T10:50:23.370Z","permissionMode":"bypassPermissions","promptSource":"sdk","userType":"external","entrypoint":"sdk-ts","cwd":"D:\\roulette-quiz","sessionId":"75127d8e-65c4-4981-88ba-3048fbf0d494","version":"2.1.205","gitBranch":"feat/de-so-mechanics"}
        {"parentUuid":"aadd8702-3f42-4107-8ac7-645dfa277629","isSidechain":false,"attachment":{"type":"skill_listing","content":"..."},"type":"attachment","uuid":"4e27c452-d779-4355-8a81-9fed9ce9043b","timestamp":"2026-07-12T10:50:23.380Z","sessionId":"75127d8e-65c4-4981-88ba-3048fbf0d494"}
        {"parentUuid":"4e27c452-d779-4355-8a81-9fed9ce9043b","isSidechain":false,"type":"assistant","uuid":"686ff709-330d-490f-8268-90a67c3ef105","timestamp":"2026-07-12T10:50:23.393Z","message":{"id":"25f12326-0da4-475f-9805-80aee135b8b9","model":"<synthetic>","role":"assistant","type":"message","content":[{"type":"text","text":"Not logged in"}]},"sessionId":"75127d8e-65c4-4981-88ba-3048fbf0d494"}
        {"type":"last-prompt","lastPrompt":"Hello","leafUuid":"686ff709-330d-490f-8268-90a67c3ef105","sessionId":"75127d8e-65c4-4981-88ba-3048fbf0d494"}
        """;

    [Fact]
    public void Reads_the_turns_out_of_a_real_transcript_and_skips_its_plumbing()
    {
        SessionTranscript transcript = _format.Read(RealTranscript);

        Assert.Equal("75127d8e-65c4-4981-88ba-3048fbf0d494", transcript.Id);
        Assert.Equal("D:\\roulette-quiz", transcript.WorkingDirectory);
        Assert.Equal("Hello", transcript.Title);
        Assert.Equal(Stamp, transcript.CreatedAt);

        // The queue line and the attachment delta are not conversation: reading them as
        // turns would put a turn in the transcript that nobody ever sent.
        Assert.Collection(
            transcript.Entries,
            entry => Assert.Equal((TranscriptEntryKind.UserMessage, "Hello"), (entry.Kind, entry.Text)),
            entry => Assert.Equal((TranscriptEntryKind.AssistantMessage, "Not logged in"), (entry.Kind, entry.Text)));
    }

    [Fact]
    public void Refuses_a_payload_that_carries_none_of_its_line_types()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => _format.Read("{\"hello\":\"world\"}"));

        Assert.Contains("Claude Code", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_transcript_with_a_line_type_but_no_readable_turn()
    {
        string empty = """{"type":"user","sessionId":"75127d8e-65c4-4981-88ba-3048fbf0d494","message":{"role":"user","content":[]}}""";

        Assert.Throws<InvalidDataException>(() => _format.Read(empty));
    }

    [Fact]
    public void Every_entry_kind_survives_a_round_trip()
    {
        SessionTranscript source = Transcript();

        SessionTranscript read = _format.Read(_format.Write(source).Single().Content);

        Assert.Equal(
            source.Entries.Select(entry => (entry.Kind, entry.Text)),
            read.Entries.Select(entry => (entry.Kind, entry.Text)));

        // A tool call that came back without its name or its call id would be a call the
        // partner cannot pair with its own result.
        TranscriptEntry call = read.Entries.Single(entry => entry.Kind == TranscriptEntryKind.ToolCall);
        Assert.Equal("read", call.ToolName);
        Assert.Equal("call_1", call.ToolCallId);
        Assert.Equal("{\"path\":\"Program.cs\"}", call.ToolArguments);

        TranscriptEntry result = read.Entries.Single(entry => entry.Kind == TranscriptEntryKind.ToolResult);
        Assert.Equal("call_1", result.ToolCallId);
        Assert.Equal("file contents", result.ToolResult);
    }

    [Fact]
    public void Writes_a_linked_chain_that_ends_in_the_last_prompt_marker()
    {
        IReadOnlyList<SessionOutputFile> files = _format.Write(Transcript());

        Assert.Equal("11111111-1111-7111-8111-111111111111.jsonl", files.Single().FileName);

        string[] lines = files.Single().Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        JsonObject[] records = [.. lines.Select(line => JsonNode.Parse(line)!.AsObject())];

        JsonObject[] turns = [.. records.Where(record => (string?)record["type"] is "user" or "assistant")];
        Assert.Null(turns[0]["parentUuid"]);

        for (int index = 1; index < turns.Length; index++)
        {
            Assert.Equal((string?)turns[index - 1]["uuid"], (string?)turns[index]["parentUuid"]);
        }

        JsonObject tail = records[^1];
        Assert.Equal("last-prompt", (string?)tail["type"]);
        Assert.Equal((string?)turns[^1]["uuid"], (string?)tail["leafUuid"]);

        // The tail records the last thing the person asked, which is what a resume reads.
        Assert.Equal("do the thing", (string?)tail["lastPrompt"]);
    }

    [Fact]
    public void Writes_the_project_directory_once_so_the_partner_can_file_the_session()
    {
        IReadOnlyList<SessionOutputFile> files = _format.Write(Transcript());

        JsonObject first = JsonNode.Parse(files.Single().Content.Split('\n')[0])!.AsObject();

        Assert.Equal("D:\\LLM", (string?)first["cwd"]);
        Assert.NotNull(first["promptId"]);
    }

    [Fact]
    public void An_id_that_is_not_a_guid_is_still_given_a_file_name_the_partner_accepts()
    {
        SessionTranscript openCode = Transcript() with { Id = "ses_04341c04cffeB52SqwtAxpilnc" };

        string fileName = ClaudeCodeSessionFormat.FileNameFor(openCode);

        Assert.EndsWith(".jsonl", fileName, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(fileName), out _));
    }

    private static SessionTranscript Transcript() => new(
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
}
