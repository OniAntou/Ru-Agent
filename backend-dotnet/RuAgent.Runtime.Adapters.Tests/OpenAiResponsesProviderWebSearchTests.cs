using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

/// <summary>OpenAI's hosted web search through the Responses adapter (ADR 0033).</summary>
public sealed class OpenAiResponsesProviderWebSearchTests
{
    private const string Model = "gpt-5.5";

    [Fact]
    public void A_search_run_declares_the_hosted_tool()
    {
        var body = JsonNode.Parse(new OpenAiResponsesModelProvider(new HttpClient()).ProjectRequestBody(Request(search: true)))!;
        var tools = body["tools"]!.AsArray().Select(tool => tool!.AsObject()).ToArray();
        Assert.Contains(tools, tool => tool["type"]!.GetValue<string>() == "function" && tool["name"]!.GetValue<string>() == "read");
        Assert.Contains(tools, tool => tool["type"]!.GetValue<string>() == "web_search" && tool.Count == 1);
        Assert.False(body.AsObject().ContainsKey("provider_web_search"));
    }

    [Fact]
    public void Other_runs_declare_no_hosted_tool()
    {
        var body = JsonNode.Parse(new OpenAiResponsesModelProvider(new HttpClient()).ProjectRequestBody(Request(search: false)))!;
        Assert.DoesNotContain(body["tools"]!.AsArray(), tool => tool!["type"]!.GetValue<string>() == "web_search");
    }

    [Fact]
    public async Task A_cited_answer_ends_with_its_sources()
    {
        var output = new JsonArray
        {
            SearchCall("ws_1"),
            new JsonObject
            {
                ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text", ["text"] = "Gold is $4,000.",
                    ["annotations"] = new JsonArray(
                        new JsonObject { ["type"] = "url_citation", ["start_index"] = 0, ["end_index"] = 4, ["url"] = "https://example.com/gold", ["title"] = "Gold today" },
                        new JsonObject { ["type"] = "url_citation", ["start_index"] = 5, ["end_index"] = 8, ["url"] = "https://example.com/gold", ["title"] = "Gold today" }),
                }),
            },
        };
        var deltas = await Collect(Text("Gold is $4,000.") + Completed(output), Request(search: true));

        Assert.Equal("Gold is $4,000.\n\nSources:\n- [Gold today](https://example.com/gold)", string.Concat(deltas.Select(delta => delta.Text)));
        Assert.True(deltas[^1].Completed);
    }

    [Fact]
    public async Task A_tool_step_keeps_its_search_call_beside_the_reasoning_it_follows()
    {
        var output = new JsonArray
        {
            new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["encrypted_content"] = "opaque", ["summary"] = new JsonArray() },
            SearchCall("ws_1"),
            new JsonObject { ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "call_1", ["name"] = "read", ["arguments"] = "{}" },
        };
        var stream = Added(0, "fc_1", "call_1") + ArgumentsDone(0, "fc_1", "{}") + Completed(output);
        var deltas = await Collect(stream, Request(search: true));

        var items = deltas[^1].ProviderContinuation!.OutputItems;
        Assert.Equal(new[] { "reasoning", "web_search_call", "function_call" }, items.Select(item => item["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_refused_hosted_search_has_its_own_failure()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Tool 'web_search' is not supported with this model.\",\"type\":\"invalid_request_error\",\"param\":\"tools\"}}",
                Encoding.UTF8, "application/json"),
        });
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var _ in new OpenAiResponsesModelProvider(new HttpClient(handler)).GenerateAsync(Request(search: true), CancellationToken.None)) { }
        });
        Assert.Equal("provider_web_search_unavailable", error.Code);
    }

    private static JsonObject SearchCall(string id) => new()
    {
        ["type"] = "web_search_call", ["id"] = id, ["status"] = "completed",
        ["action"] = new JsonObject { ["type"] = "search", ["query"] = "gold price" },
    };

    private static ModelRequest Request(bool search)
    {
        var settings = new JsonObject
        {
            ["model_tools"] = new JsonArray(new JsonObject
            {
                ["tool_id"] = "read", ["tool_version"] = "1.0.0", ["description"] = "Read",
                ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            }),
        };
        if (search) settings[ModelRequest.ProviderWebSearchSetting] = true;
        return new ModelRequest(Model, "system", "hello", Array.Empty<ModelTurn>(), settings,
            new Uri("https://api.openai.com/v1/responses"), "synthetic-openai", 1024, nativeToolCalling: true);
    }

    private static async Task<List<ModelDelta>> Collect(string stream, ModelRequest request)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") });
        var deltas = new List<ModelDelta>();
        await foreach (var delta in new OpenAiResponsesModelProvider(new HttpClient(handler)).GenerateAsync(request, CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    private static string Event(string name, JsonObject data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";

    private static string Text(string text) =>
        Event("response.output_text.delta", new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = text });

    private static string Added(int index, string itemId, string callId) =>
        Event("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added", ["output_index"] = index,
            ["item"] = new JsonObject { ["id"] = itemId, ["type"] = "function_call", ["call_id"] = callId, ["name"] = "read", ["arguments"] = "" },
        });

    private static string ArgumentsDone(int index, string itemId, string arguments) =>
        Event("response.function_call_arguments.done", new JsonObject
        {
            ["type"] = "response.function_call_arguments.done", ["output_index"] = index, ["item_id"] = itemId, ["arguments"] = arguments,
        });

    private static string Completed(JsonArray output) =>
        Event("response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject { ["model"] = Model, ["status"] = "completed", ["output"] = output },
        });

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
