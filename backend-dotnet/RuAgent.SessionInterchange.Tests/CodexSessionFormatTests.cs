using System.Text.Json.Nodes;
using RuAgent.SessionInterchange;
using Xunit;

namespace RuAgent.SessionInterchange.Tests;

public sealed class CodexSessionFormatTests
{
    private static readonly DateTimeOffset Stamp = new DateTimeOffset(2026, 9, 7, 15, 13, 44, TimeSpan.Zero).AddMilliseconds(40);

    private readonly CodexSessionFormat _format = new();

    /// <summary>
    /// The shape below is copied from a real rollout on this machine: the meta line
    /// first, the turn events around it, and one response_item per thing the model did.
    /// </summary>
    private const string RealRollout = """
        {"timestamp":"2026-09-07T15:14:34.810Z","ordinal":0,"type":"session_meta","payload":{"session_id":"01a07c6e-aff5-7fa3-b7f6-9e7cd8096ae4","id":"01a07c6e-aff5-7fa3-b7f6-9e7cd8096ae4","timestamp":"2026-09-07T15:13:44.040Z","cwd":"D:\\LLM","originator":"Codex Desktop","cli_version":"0.153.4","source":"vscode","thread_source":"user","model_provider":"openai","base_instructions":{"text":"You are Codex"}}}
        {"timestamp":"2026-09-07T15:14:34.820Z","ordinal":1,"type":"event_msg","payload":{"type":"task_started","turn_id":"01a07c6e-9ad7-73b3-9429-5e84c82f62d2","started_at":1786306403}}
        {"timestamp":"2026-09-07T15:14:35.000Z","ordinal":2,"type":"response_item","payload":{"type":"message","id":"msg_1","role":"user","content":[{"type":"input_text","text":"Fix the widget"}]}}
        {"timestamp":"2026-09-07T15:14:36.000Z","ordinal":3,"type":"response_item","payload":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Planning the edit"}],"content":null}}
        {"timestamp":"2026-09-07T15:14:37.000Z","ordinal":4,"type":"response_item","payload":{"type":"custom_tool_call","id":"ctc_1","status":"completed","call_id":"call_1","name":"exec","input":"const r = await tools.exec_command({});"}}
        {"timestamp":"2026-09-07T15:14:38.000Z","ordinal":5,"type":"response_item","payload":{"type":"custom_tool_call_output","id":"ctco_1","call_id":"call_1","output":[{"type":"input_text","text":"Script completed"}]}}
        {"timestamp":"2026-09-07T15:14:39.000Z","ordinal":6,"type":"response_item","payload":{"type":"message","id":"msg_2","role":"assistant","content":[{"type":"output_text","text":"Fixed the widget"}],"phase":"final_answer"}}
        {"timestamp":"2026-09-07T15:14:40.000Z","ordinal":7,"type":"event_msg","payload":{"type":"task_complete","turn_id":"01a07c6e-9ad7-73b3-9429-5e84c82f62d2","last_agent_message":"Fixed the widget"}}
        """;

    [Fact]
    public void Reads_the_turns_out_of_a_real_rollout_and_skips_its_turn_events()
    {
        SessionTranscript transcript = _format.Read(RealRollout);

        Assert.Equal("01a07c6e-aff5-7fa3-b7f6-9e7cd8096ae4", transcript.Id);
        Assert.Equal("D:\\LLM", transcript.WorkingDirectory);
        Assert.Equal("Fix the widget", transcript.Title);
        Assert.Equal(Stamp, transcript.CreatedAt);

        Assert.Collection(
            transcript.Entries,
            entry => Assert.Equal(TranscriptEntryKind.UserMessage, entry.Kind),
            entry => Assert.Equal((TranscriptEntryKind.Reasoning, "Planning the edit"), (entry.Kind, entry.Text)),
            entry => Assert.Equal(TranscriptEntryKind.ToolCall, entry.Kind),
            entry => Assert.Equal(TranscriptEntryKind.ToolResult, entry.Kind),
            entry => Assert.Equal((TranscriptEntryKind.AssistantMessage, "Fixed the widget"), (entry.Kind, entry.Text)));

        // The call and its output have to keep the id the model used, or the partner
        // shows a call that never returns.
        Assert.Equal("call_1", transcript.Entries[2].ToolCallId);
        Assert.Equal("call_1", transcript.Entries[3].ToolCallId);
        Assert.Equal("Planning the edit", transcript.Entries[1].Text);
    }

    [Fact]
    public void Refuses_a_payload_that_is_not_a_rollout()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => _format.Read("[1,2,3]"));

        Assert.Contains("Codex rollout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_entry_kind_survives_a_round_trip()
    {
        SessionTranscript source = Transcript();

        SessionTranscript read = _format.Read(_format.Write(source)[0].Content);

        Assert.Equal(
            source.Entries.Select(entry => (entry.Kind, entry.Text)),
            read.Entries.Select(entry => (entry.Kind, entry.Text)));
        Assert.Equal(
            source.Entries.Where(entry => entry.Kind == TranscriptEntryKind.ToolCall).Select(entry => entry.ToolCallId),
            read.Entries.Where(entry => entry.Kind == TranscriptEntryKind.ToolCall).Select(entry => entry.ToolCallId));
        Assert.Equal("D:\\LLM", read.WorkingDirectory);
        Assert.Equal("do the thing", read.Title);
    }

    [Fact]
    public void Writes_a_meta_line_first_ordinals_that_never_repeat_and_a_finished_turn()
    {
        IReadOnlyList<SessionOutputFile> files = _format.Write(Transcript());

        JsonObject[] records =
        [
            .. files[0].Content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)!.AsObject()),
        ];

        Assert.Equal("session_meta", (string?)records[0]["type"]);
        Assert.Equal("11111111-1111-7111-8111-111111111111", (string?)records[0]["payload"]!["session_id"]);

        // Ordering is positional here: there is no parent link to repair a line that
        // lands in the wrong place, so the ordinal is the only thing holding the order.
        int?[] ordinals = [.. records.Select(record => (int?)record["ordinal"])];
        Assert.Equal(Enumerable.Range(0, records.Length).Select(index => (int?)index).ToArray(), ordinals);

        JsonObject last = records[^1];
        Assert.Equal("event_msg", (string?)last["type"]);
        Assert.Equal("task_complete", (string?)last["payload"]!["type"]);
        Assert.Equal("done", (string?)last["payload"]!["last_agent_message"]);
    }

    [Fact]
    public void Writes_the_index_line_the_thread_picker_reads()
    {
        IReadOnlyList<SessionOutputFile> files = _format.Write(Transcript());

        SessionOutputFile index = files[1];
        Assert.Equal("session_index.jsonl", index.FileName);

        JsonObject entry = JsonNode.Parse(index.Content.Trim())!.AsObject();
        Assert.Equal("11111111-1111-7111-8111-111111111111", (string?)entry["id"]);
        Assert.Equal("Bring a session across", (string?)entry["thread_name"]);
    }

    [Fact]
    public void The_rollout_file_is_named_by_date_and_thread_id_like_the_partners_own()
    {
        string fileName = CodexSessionFormat.FileNameFor(Transcript());

        Assert.StartsWith("rollout-2026-09-07T15-13-44-", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".jsonl", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_from_another_agent_is_replaced_by_a_sortable_uuid()
    {
        SessionTranscript openCode = Transcript() with { Id = "ses_04341c04cffeB52SqwtAxpilnc" };

        string fileName = CodexSessionFormat.FileNameFor(openCode);
        string id = Path.GetFileNameWithoutExtension(fileName)["rollout-2026-09-07T15-13-44-".Length..];

        Assert.True(Guid.TryParse(id, out _));

        // Codex mints UUIDv7, whose version nibble is 7: ids then sort by when they
        // happened instead of by chance.
        Assert.Equal('7', id[14]);
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
