using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

/// <summary>Anthropic's hosted web search through the Messages adapter (ADR 0033).</summary>
public sealed class AnthropicProviderWebSearchTests
{
    private const string Model = "claude-sonnet-5-5";

    [Fact]
    public void A_search_run_declares_the_hosted_tool_beside_Ru_tools()
    {
        var body = JsonNode.Parse(new AnthropicMessagesModelProvider(new HttpClient()).ProjectRequestBody(Request(search: true)))!;
        var tools = body["tools"]!.AsArray().Select(tool => tool!.AsObject()).ToArray();
        Assert.Contains(tools, tool => tool["name"]!.GetValue<string>() == "read");
        var search = Assert.Single(tools, tool => tool["type"]?.GetValue<string>() == "web_search_20250305");
        Assert.Equal("web_search", search["name"]!.GetValue<string>());
        Assert.Equal(5, search["max_uses"]!.GetValue<int>());
        Assert.False(body.AsObject().ContainsKey("provider_web_search"));
    }

    [Fact]
    public void Other_runs_declare_no_hosted_tool()
    {
        var body = new AnthropicMessagesModelProvider(new HttpClient()).ProjectRequestBody(Request(search: false));
        Assert.DoesNotContain("web_search_20250305", body);
    }

    [Fact]
    public void A_Ru_tool_named_web_search_cannot_sit_beside_the_hosted_one()
    {
        var request = Request(search: true, toolName: "web_search");
        Assert.Throws<ModelProviderException>(() => new AnthropicMessagesModelProvider(new HttpClient()).ProjectRequestBody(request));
    }

    [Fact]
    public async Task A_cited_answer_streams_its_text_then_a_Sources_list()
    {
        var stream = Message("end_turn",
            Text(0, "Let me check. "),
            ServerToolUse(1, "srvtoolu_1", "gold price"),
            SearchResult(2, "srvtoolu_1"),
            CitedText(3, "Gold is $4,000.", Citation("https://example.com/gold", "Gold today")),
            CitedText(4, " Up 2%.", Citation("https://example.com/gold", "Gold today"), Citation("https://news.example/market", "Markets")));
        var deltas = await Collect(new Responses(stream), Request(search: true));

        var text = string.Concat(deltas.Select(delta => delta.Text));
        Assert.StartsWith("Let me check. Gold is $4,000. Up 2%.", text);
        Assert.EndsWith("\n\nSources:\n- [Gold today](https://example.com/gold)\n- [Markets](https://news.example/market)", text);
        var terminal = deltas[^1];
        Assert.True(terminal.Completed);
        Assert.Equal("stop", terminal.FinishReason);
        Assert.Null(terminal.ProviderContinuation);
    }

    [Fact]
    public async Task A_long_cited_answer_is_not_cut_off_by_the_block_bound()
    {
        var blocks = new List<string>();
        for (var index = 0; index < 60; index++)
            blocks.Add(index % 2 == 0 ? Text(index, "plain ") : CitedText(index, "cited ", Citation($"https://example.com/{index}", "Source")));
        var deltas = await Collect(new Responses(Message("end_turn", blocks.ToArray())), Request(search: true));
        Assert.True(deltas[^1].Completed);
        Assert.Contains("Sources:", string.Concat(deltas.Select(delta => delta.Text)));
    }

    [Fact]
    public async Task A_tool_step_keeps_its_search_blocks_and_citations_for_replay()
    {
        var stream = Message("tool_use",
            ServerToolUse(0, "srvtoolu_1", "dotnet release"),
            SearchResult(1, "srvtoolu_1"),
            CitedText(2, "Found it.", Citation("https://example.com/dotnet", "Release")),
            ToolUse(3, "toolu_1"));
        var deltas = await Collect(new Responses(stream), Request(search: true));

        var continuation = deltas[^1].ProviderContinuation!;
        var types = continuation.OutputItems.Select(item => item["type"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "server_tool_use", "web_search_tool_result", "text", "tool_use" }, types);
        Assert.Equal("dotnet release", continuation.OutputItems[0]["input"]!["query"]!.GetValue<string>());
        Assert.Equal("EqgfCio", continuation.OutputItems[1]["content"]![0]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("Eo8BCio", continuation.OutputItems[2]["citations"]![0]!["encrypted_index"]!.GetValue<string>());
        Assert.DoesNotContain("Sources:", string.Concat(deltas.Select(delta => delta.Text)));
    }

    [Fact]
    public async Task A_continuation_past_its_bounds_drops_the_search_instead_of_failing()
    {
        var blocks = new List<string> { ServerToolUse(0, "srvtoolu_1", "q"), SearchResult(1, "srvtoolu_1") };
        for (var index = 2; index < 42; index++)
            blocks.Add(CitedText(index, "x", Citation($"https://example.com/{index}", "Source")));
        blocks.Add(ToolUse(42, "toolu_1"));
        var deltas = await Collect(new Responses(Message("tool_use", blocks.ToArray())), Request(search: true));

        var items = deltas[^1].ProviderContinuation!.OutputItems;
        Assert.Equal(new[] { "text", "tool_use" }, items.Select(item => item["type"]!.GetValue<string>()));
        Assert.Equal(new string('x', 40), items[0]["text"]!.GetValue<string>());
        Assert.False(items[0].ContainsKey("citations"));
    }

    [Fact]
    public async Task A_paused_turn_is_sent_back_and_the_same_step_finishes()
    {
        var handler = new Responses(
            Message("pause_turn", Text(0, "Searching. "), ServerToolUse(1, "srvtoolu_1", "q"), SearchResult(2, "srvtoolu_1")),
            Message("end_turn", Text(0, "Done.")));
        var deltas = await Collect(handler, Request(search: true));

        Assert.Equal("Searching. Done.", string.Concat(deltas.Select(delta => delta.Text)));
        Assert.Single(deltas, delta => delta.Completed);
        Assert.Equal(2, handler.Bodies.Count);
        var resent = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray();
        var paused = resent[^1]!.AsObject();
        Assert.Equal("assistant", paused["role"]!.GetValue<string>());
        Assert.Equal(new[] { "text", "server_tool_use", "web_search_tool_result" },
            paused["content"]!.AsArray().Select(block => block!["type"]!.GetValue<string>()));
        Assert.Contains("web_search_20250305", handler.Bodies[1]);
    }

    [Fact]
    public async Task A_turn_that_keeps_pausing_fails_with_a_named_code()
    {
        // Each continuation adds new searches; ids never repeat within a turn.
        string Paused(int turn) => Message("pause_turn", ServerToolUse(0, $"srvtoolu_{turn}", "q"), SearchResult(1, $"srvtoolu_{turn}"));
        var handler = new Responses(Paused(1), Paused(2), Paused(3), Paused(4), Paused(5));
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(handler, Request(search: true)));
        Assert.Equal("provider_search_paused", error.Code);
        Assert.Equal(AnthropicMessagesModelProvider.MaximumPausedTurnContinuations + 1, handler.Bodies.Count);
    }

    [Fact]
    public async Task Search_blocks_in_a_run_without_the_hosted_tool_are_malformed()
    {
        var stream = Message("end_turn", ServerToolUse(0, "srvtoolu_1", "q"));
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(new Responses(stream), Request(search: false)));
        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task A_refused_hosted_search_has_its_own_failure()
    {
        var handler = new Responses(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Web search is not enabled for this organization.\"}}",
                Encoding.UTF8, "application/json"),
        });
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(handler, Request(search: true)));
        Assert.Equal("provider_web_search_unavailable", error.Code);
    }

    private static ModelRequest Request(bool search, string toolName = "read")
    {
        var settings = new JsonObject
        {
            ["model_tools"] = new JsonArray(new JsonObject
            {
                ["tool_id"] = toolName, ["tool_version"] = "1", ["description"] = "A tool",
                ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            }),
        };
        if (search) settings[ModelRequest.ProviderWebSearchSetting] = true;
        return new ModelRequest(Model, "system", "hello", Array.Empty<ModelTurn>(), settings,
            new Uri("https://api.anthropic.com/v1/messages"), "synthetic-anthropic", 1024, nativeToolCalling: true);
    }

    private static async Task<List<ModelDelta>> Collect(HttpMessageHandler handler, ModelRequest request)
    {
        var deltas = new List<ModelDelta>();
        await foreach (var delta in new AnthropicMessagesModelProvider(new HttpClient(handler)).GenerateAsync(request, CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    private static string Event(string name, JsonObject data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";

    private static string Message(string stopReason, params string[] blocks) =>
        Event("message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["model"] = Model, ["content"] = new JsonArray() } })
        + string.Concat(blocks)
        + Event("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stopReason } })
        + Event("message_stop", new JsonObject { ["type"] = "message_stop" });

    private static string Start(int index, JsonObject block) =>
        Event("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block });

    private static string Delta(int index, JsonObject delta) =>
        Event("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });

    private static string Stop(int index) =>
        Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });

    private static string Text(int index, string text) =>
        Start(index, new JsonObject { ["type"] = "text", ["text"] = "" })
        + Delta(index, new JsonObject { ["type"] = "text_delta", ["text"] = text })
        + Stop(index);

    private static string CitedText(int index, string text, params JsonObject[] citations) =>
        Start(index, new JsonObject { ["type"] = "text", ["text"] = "" })
        + string.Concat(citations.Select(citation => Delta(index, new JsonObject { ["type"] = "citations_delta", ["citation"] = citation })))
        + Delta(index, new JsonObject { ["type"] = "text_delta", ["text"] = text })
        + Stop(index);

    private static JsonObject Citation(string url, string title) => new()
    {
        ["type"] = "web_search_result_location", ["url"] = url, ["title"] = title,
        ["encrypted_index"] = "Eo8BCio", ["cited_text"] = "cited",
    };

    private static string ServerToolUse(int index, string id, string query) =>
        Start(index, new JsonObject { ["type"] = "server_tool_use", ["id"] = id, ["name"] = "web_search", ["input"] = new JsonObject() })
        + Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = new JsonObject { ["query"] = query }.ToJsonString() })
        + Stop(index);

    private static string SearchResult(int index, string toolUseId) =>
        Start(index, new JsonObject
        {
            ["type"] = "web_search_tool_result", ["tool_use_id"] = toolUseId,
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search_result", ["url"] = "https://example.com/gold", ["title"] = "Gold today",
                ["encrypted_content"] = "EqgfCio", ["page_age"] = "October 28, 2026",
            }),
        })
        + Stop(index);

    private static string ToolUse(int index, string id) =>
        Start(index, new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "read", ["input"] = new JsonObject() })
        + Delta(index, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{}" })
        + Stop(index);

    /// <summary>Answers each request with the next canned response and records the bodies.</summary>
    private sealed class Responses : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<string> Bodies { get; } = [];

        public Responses(params string[] streams)
            : this(streams.Select(stream => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
            }).ToArray())
        {
        }

        public Responses(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("No response left.");
        }
    }
}
