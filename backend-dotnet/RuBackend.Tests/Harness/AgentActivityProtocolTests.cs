using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentActivityProtocolTests
{
    [Fact]
    public void Parse_accepts_a_todo_list_and_round_trips_it()
    {
        const string json = """
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded",
             "todos":[{"content":"Read the catalog","status":"completed"},
                      {"content":"Add the tool","status":"in_progress"}]}
            """;

        var wire = AgentActivityWireCodec.Parse(json);

        Assert.NotNull(wire.Todos);
        Assert.Equal(2, wire.Todos!.Count);
        Assert.Equal("Read the catalog", wire.Todos[0].Content);
        Assert.Equal("completed", wire.Todos[0].Status);
        Assert.Equal("in_progress", wire.Todos[1].Status);
        Assert.Equal(AgentActivityKind.Todo, wire.Kind);

        var serialized = AgentActivityWireCodec.Serialize(wire);
        Assert.Contains("todos", serialized, StringComparison.Ordinal);
        Assert.Equal(2, AgentActivityWireCodec.Parse(serialized).Todos!.Count);
    }

    [Fact]
    public void A_todo_activity_without_a_list_is_still_valid()
    {
        // The activity is a general envelope; a todo activity that clears the list
        // carries no rows rather than an empty array.
        var wire = AgentActivityWireCodec.Parse(
            """{"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded"}""");

        Assert.Null(wire.Todos);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("")]
    [InlineData("Pending")]
    public void Parse_rejects_a_todo_with_an_unknown_status(string status)
    {
        var json = $$"""
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded",
             "todos":[{"content":"x","status":"{{status}}"}]}
            """;

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Fact]
    public void Parse_rejects_a_todo_with_an_unknown_key()
    {
        const string json = """
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded",
             "todos":[{"content":"x","status":"pending","priority":1}]}
            """;

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_rejects_a_todo_with_blank_content(string content)
    {
        var json = $$"""
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded",
             "todos":[{"content":"{{content}}","status":"pending"}]}
            """;

        // An empty array entry still reaches the validator, so this asserts the row
        // check rather than the envelope check.
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Fact]
    public void Parse_rejects_more_todos_than_the_bound()
    {
        string items = string.Join(
            ",",
            Enumerable.Range(0, 65).Select(index => $$"""{"content":"t{{index}}","status":"pending"}"""));
        var json = $$"""
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded","todos":[{{items}}]}
            """;

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Fact]
    public void Parse_accepts_exactly_the_todo_bound()
    {
        string items = string.Join(
            ",",
            Enumerable.Range(0, 64).Select(index => $$"""{"content":"t{{index}}","status":"pending"}"""));
        var json = $$"""
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded","todos":[{{items}}]}
            """;

        Assert.Equal(64, AgentActivityWireCodec.Parse(json).Todos!.Count);
    }

    [Fact]
    public void Parse_rejects_todo_content_over_the_bound()
    {
        string content = new string('x', 201);
        var json = $$"""
            {"activity_id":"act_1","kind":"todo","state":"completed","safe_code":"succeeded",
             "todos":[{"content":"{{content}}","status":"pending"}]}
            """;

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Fact]
    public void Serialize_omits_unavailable_optional_fields_and_uses_wire_tokens()
    {
        var activity = new AgentActivityWire(
            "act_123",
            AgentActivityKind.Analyze,
            AgentActivityState.Completed,
            "ChatView.xaml.cs",
            150,
            200,
            "OnChatInputPreviewKeyDown",
            null,
            null,
            null,
            5000,
            "succeeded");

        var json = AgentActivityWireCodec.Serialize(activity);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("act_123", root.GetProperty("activity_id").GetString());
        Assert.Equal("analyze", root.GetProperty("kind").GetString());
        Assert.Equal("completed", root.GetProperty("state").GetString());
        Assert.Equal("ChatView.xaml.cs", root.GetProperty("file_name").GetString());
        Assert.Equal(150, root.GetProperty("line_start").GetInt32());
        Assert.Equal(200, root.GetProperty("line_end").GetInt32());
        Assert.Equal(5000, root.GetProperty("duration_ms").GetInt32());
        Assert.Equal("succeeded", root.GetProperty("safe_code").GetString());
        Assert.False(root.TryGetProperty("count", out _));
        Assert.False(root.TryGetProperty("added", out _));
        Assert.False(root.TryGetProperty("removed", out _));
    }

    [Fact]
    public void Parse_accepts_valid_activity_with_all_safe_metadata()
    {
        const string json = """
            {
              "activity_id": "act_abc123",
              "kind": "edit",
              "state": "started",
              "file_name": "ChatView.xaml",
              "line_start": 1,
              "line_end": 42,
              "symbol": "ChatView",
              "count": 5,
              "added": 12,
              "removed": 3,
              "duration_ms": 250,
              "safe_code": "in_progress"
            }
            """;

        var activity = AgentActivityWireCodec.Parse(json);

        Assert.Equal("act_abc123", activity.ActivityId);
        Assert.Equal(AgentActivityKind.Edit, activity.Kind);
        Assert.Equal(AgentActivityState.Started, activity.State);
        Assert.Equal("ChatView.xaml", activity.FileName);
        Assert.Equal(1, activity.LineStart);
        Assert.Equal(42, activity.LineEnd);
        Assert.Equal("ChatView", activity.Symbol);
        Assert.Equal(5, activity.Count);
        Assert.Equal(12, activity.Added);
        Assert.Equal(3, activity.Removed);
        Assert.Equal(250, activity.DurationMs);
        Assert.Equal("in_progress", activity.SafeCode);
    }

    [Fact]
    public void Parse_accepts_existing_safe_code_tokens_with_dots()
    {
        var activity = AgentActivityWireCodec.Parse(
            "{\"activity_id\":\"act_1\",\"kind\":\"status\",\"state\":\"failed\",\"safe_code\":\"provider.failed\"}");

        Assert.Equal("provider.failed", activity.SafeCode);
    }

    [Fact]
    public void Every_declared_kind_and_state_has_a_valid_wire_token()
    {
        foreach (var kind in Enum.GetValues<AgentActivityKind>())
        foreach (var state in Enum.GetValues<AgentActivityState>())
        {
            var activity = new AgentActivityWire(
                "act_matrix",
                kind,
                state,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "succeeded");

            var parsed = AgentActivityWireCodec.Parse(AgentActivityWireCodec.Serialize(activity));

            Assert.Equal(kind, parsed.Kind);
            Assert.Equal(state, parsed.State);
        }
    }

    [Fact]
    public void Detail_round_trips_and_stays_optional()
    {
        var withDetail = new AgentActivityWire(
            "act_matrix", AgentActivityKind.Thinking, AgentActivityState.Completed,
            SafeCode: "succeeded", Detail: "Considering folder scope.\nSecond line.");
        Assert.Equal("Considering folder scope.\nSecond line.", AgentActivityWireCodec.Parse(
            AgentActivityWireCodec.Serialize(withDetail)).Detail);

        var withoutDetail = new AgentActivityWire(
            "act_matrix", AgentActivityKind.Thinking, AgentActivityState.Completed);
        Assert.Null(AgentActivityWireCodec.Parse(
            AgentActivityWireCodec.Serialize(withoutDetail)).Detail);
    }

    [Fact]
    public void Parse_rejects_oversized_or_nul_detail()
    {
        var oversized = JsonSerializer.Serialize(new
        {
            activity_id = "act_1",
            kind = "thinking",
            state = "completed",
            safe_code = "succeeded",
            detail = new string('x', 4_001),
        });
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(oversized));

        var nul = "{\"activity_id\":\"act_1\",\"kind\":\"thinking\",\"state\":\"completed\",\"safe_code\":\"succeeded\",\"detail\":\"a\\u0000b\"}";
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(nul));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("arguments")]
    [InlineData("argv")]
    [InlineData("path")]
    [InlineData("code")]
    [InlineData("diff")]
    [InlineData("output")]
    [InlineData("script")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("body")]
    [InlineData("environment")]
    [InlineData("exception")]
    [InlineData("exception_message")]
    public void Parse_rejects_content_bearing_or_unknown_fields(string fieldName)
    {
        var json = $"{{\"activity_id\":\"act_1\",\"kind\":\"status\",\"state\":\"failed\",\"safe_code\":\"failed\",\"{fieldName}\":\"secret\"}}";

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Theory]
    [InlineData("dir/file.cs")]
    [InlineData("dir\\file.cs")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("ChatView.xaml.")]
    [InlineData("ChatView.xaml ")]
    public void Parse_rejects_non_basename_file_names(string fileName)
    {
        var json = JsonSerializer.Serialize(new
        {
            activity_id = "act_1",
            kind = "analyze",
            state = "completed",
            file_name = fileName,
            safe_code = "succeeded",
        });

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Theory]
    [InlineData("\u0001bad")]
    [InlineData("not a safe symbol")]
    [InlineData("symbol/with/slash")]
    public void Parse_rejects_control_or_unbounded_symbol_tokens(string symbol)
    {
        var json = JsonSerializer.Serialize(new
        {
            activity_id = "act_1",
            kind = "search",
            state = "completed",
            symbol,
            safe_code = "succeeded",
        });

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Theory]
    [InlineData("{\"activity_id\":\"act_1\",\"state\":\"completed\",\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":1,\"state\":\"completed\",\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":2,\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":\"completed\",\"line_start\":0,\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":\"completed\",\"line_start\":20,\"line_end\":10,\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":\"completed\",\"line_start\":1000001,\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":\"completed\",\"count\":-1,\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":\"completed\",\"duration_ms\":86400001,\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"unknown\",\"state\":\"completed\",\"safe_code\":\"succeeded\"}")]
    [InlineData("{\"activity_id\":\"act_1\",\"kind\":\"analyze\",\"state\":\"unknown\",\"safe_code\":\"succeeded\"}")]
    public void Parse_rejects_invalid_ranges_counts_durations_and_enum_tokens(string json)
    {
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Theory]
    [InlineData("activity_1")]
    [InlineData("act-")]
    [InlineData("act_")]
    public void Parse_rejects_invalid_activity_ids(string activityId)
    {
        var json = JsonSerializer.Serialize(new
        {
            activity_id = activityId,
            kind = "status",
            state = "started",
            safe_code = "in_progress",
        });

        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse(json));
    }

    [Fact]
    public void Serialize_Includes_ToolDisplay_When_Set()
    {
        var activity = new AgentActivityWire("act_abc123", AgentActivityKind.Analyze, AgentActivityState.Completed, ToolDisplay: "View");
        var json = AgentActivityWireCodec.Serialize(activity);
        Assert.Contains("tool_display", json, StringComparison.Ordinal);
        var parsed = AgentActivityWireCodec.Parse(json);
        Assert.Equal("View", parsed.ToolDisplay);
    }

    [Fact]
    public void Parse_Old_Payload_Without_ToolDisplay_Succeeds()
    {
        var parsed = AgentActivityWireCodec.Parse("{\"activity_id\":\"act_abc123\",\"kind\":\"analyze\",\"state\":\"completed\",\"safe_code\":\"succeeded\"}");
        Assert.Null(parsed.ToolDisplay);
    }

    [Fact]
    public void Parse_Rejects_ToolDisplay_With_Slash()
    {
        Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse("{\"activity_id\":\"act_abc123\",\"kind\":\"analyze\",\"state\":\"completed\",\"tool_display\":\"a/b\",\"safe_code\":\"succeeded\"}"));
    }
}
