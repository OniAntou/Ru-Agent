using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class OpenAiResponsesIncompleteTests
{
    [Fact]
    public async Task Response_incomplete_for_max_output_tokens_discards_partial_calls_and_keeps_usage()
    {
        var sse = Event("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["type"] = "function_call", ["id"] = "item-1", ["call_id"] = "call-1",
                ["name"] = "files.read", ["arguments"] = ""
            }
        }) + Event("response.function_call_arguments.delta", new JsonObject
        {
            ["type"] = "response.function_call_arguments.delta",
            ["output_index"] = 0,
            ["item_id"] = "item-1",
            ["delta"] = "{\"path\":"
        }) + Event("response.incomplete", new JsonObject
        {
            ["type"] = "response.incomplete",
            ["response"] = new JsonObject
            {
                ["model"] = "test-model",
                ["status"] = "incomplete",
                ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" },
                ["output"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "function_call",
                        ["id"] = "item-1",
                        ["call_id"] = "call-1",
                        ["name"] = "files.read",
                        ["arguments"] = "{\"path\":"
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 17,
                    ["output_tokens"] = 9,
                    ["total_tokens"] = 26
                }
            }
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(new SseHandler(sse)));

        var deltas = await CollectAsync(provider.GenerateAsync(new ModelRequest(
            "test-model", "system", "prompt", [], new JsonObject(),
            new Uri("https://provider.invalid/v1/responses"), "synthetic-key"), CancellationToken.None));

        var terminal = Assert.Single(deltas);
        Assert.True(terminal.Completed);
        Assert.Equal("length", terminal.FinishReason);
        Assert.Empty(terminal.ToolFragments);
        Assert.NotNull(terminal.Usage);
        Assert.Equal(17, terminal.Usage.InputTokens);
        Assert.Equal(9, terminal.Usage.OutputTokens);
    }

    [Fact]
    public async Task Response_incomplete_for_content_filter_returns_the_named_finish_reason_without_partial_calls()
    {
        var sse = Event("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["type"] = "function_call", ["id"] = "item-1", ["call_id"] = "call-1",
                ["name"] = "files.read", ["arguments"] = ""
            }
        }) + Event("response.function_call_arguments.delta", new JsonObject
        {
            ["type"] = "response.function_call_arguments.delta",
            ["output_index"] = 0,
            ["item_id"] = "item-1",
            ["delta"] = "{\"path\":"
        }) + Event("response.incomplete", new JsonObject
        {
            ["type"] = "response.incomplete",
            ["response"] = new JsonObject
            {
                ["model"] = "test-model",
                ["status"] = "incomplete",
                ["incomplete_details"] = new JsonObject { ["reason"] = "content_filter" },
                ["usage"] = new JsonObject { ["input_tokens"] = 11, ["output_tokens"] = 3, ["total_tokens"] = 14 }
            }
        });
        var provider = new OpenAiResponsesModelProvider(new HttpClient(new SseHandler(sse)));

        var deltas = await CollectAsync(provider.GenerateAsync(new ModelRequest(
            "test-model", "system", "prompt", [], new JsonObject(),
            new Uri("https://provider.invalid/v1/responses"), "synthetic-key"), CancellationToken.None));

        var terminal = Assert.Single(deltas);
        Assert.True(terminal.Completed);
        Assert.Equal("content_filter", terminal.FinishReason);
        Assert.Empty(terminal.ToolFragments);
        Assert.Equal(11, terminal.Usage!.InputTokens);
    }

    private static string Event(string name, JsonObject data) =>
        $"event: {name}\ndata: {data.ToJsonString()}\n\n";

    private static async Task<List<ModelDelta>> CollectAsync(IAsyncEnumerable<ModelDelta> deltas)
    {
        var result = new List<ModelDelta>();
        await foreach (var delta in deltas) result.Add(delta);
        return result;
    }

    private sealed class SseHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
            });
    }
}
