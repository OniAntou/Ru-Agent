using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class IndexedChatToolCallTests
{
    [Fact]
    public async Task Interleaved_argument_fragments_stay_with_their_call_index()
    {
        var result = await RunAsync(
            Chunk(Call(0, "call-a", "files.read", "{\"path\":"), Call(1, "call-b", "files.read", "{\"path\":")),
            Chunk(Call(0, null, null, "\"a.txt\"}")),
            Chunk(Call(1, null, null, "\"b.txt\"}")));
        Assert.Equal(new[] { "call-a", "call-b" }, result.ToolCalls.Select(call => call.ProviderCallId));
        Assert.Equal(new[] { "a.txt", "b.txt" }, result.ToolCalls.Select(call => call.Arguments.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Repeated_call_id_on_a_continuation_does_not_start_a_second_call()
    {
        var result = await RunAsync(
            Chunk(Call(0, "call-a", "files.read", "{\"path\":")),
            Chunk(Call(0, "call-a", null, "\"a.txt\"}")));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call-a", call.ProviderCallId);
        Assert.Equal("a.txt", call.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Function_name_fragments_are_assembled_before_tool_lookup()
    {
        var result = await RunAsync(
            Chunk(Call(0, "call-a", "files.", "")),
            Chunk(Call(0, null, "read", "{\"path\":\"a.txt\"}")));
        Assert.Equal("files.read", Assert.Single(result.ToolCalls).ToolId);
    }

    [Fact]
    public async Task Indexed_calls_without_provider_ids_still_have_distinct_identities()
    {
        var result = await RunAsync(Chunk(
            Call(0, null, "files.read", "{\"path\":\"a.txt\"}"),
            Call(1, null, "files.read", "{\"path\":\"b.txt\"}")));
        Assert.Equal(2, result.ToolCalls.Count);
        Assert.Equal(2, result.ToolCalls.Select(call => call.ProviderCallId).Distinct().Count());
        Assert.All(result.ToolCalls, call => Assert.False(string.IsNullOrWhiteSpace(call.ProviderCallId)));
        Assert.Equal(new[] { "a.txt", "b.txt" }, result.ToolCalls.Select(call => call.Arguments.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Calls_follow_index_order_when_opening_chunks_arrive_out_of_order()
    {
        var result = await RunAsync(
            Chunk(Call(1, "call-b", "files.read", "{\"path\":\"b.txt\"}")),
            Chunk(Call(0, "call-a", "files.read", "{\"path\":\"a.txt\"}")));
        Assert.Equal(new[] { "call-a", "call-b" }, result.ToolCalls.Select(call => call.ProviderCallId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public async Task Out_of_range_call_indices_are_rejected(int index) =>
        await AssertRejectedAsync(Chunk(Call(index, "call-a", "files.read", "{}")));

    [Fact]
    public async Task Changing_the_id_of_an_existing_index_is_rejected() =>
        await AssertRejectedAsync(Chunk(Call(0, "call-a", "files.read", "{")),
            Chunk(Call(0, "call-b", null, "}")));

    [Fact]
    public async Task Reusing_one_id_for_two_indices_is_rejected() =>
        await AssertRejectedAsync(Chunk(Call(0, "call-a", "files.read", "{}"),
            Call(1, "call-a", "files.read", "{}")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_indexed_and_unindexed_calls_are_rejected(bool indexedFirst)
    {
        var indexed = Call(0, "call-a", "files.read", "{}");
        var unindexed = Call(1, "call-b", "files.read", "{}");
        unindexed.Remove("index");
        await AssertRejectedAsync(indexedFirst ? Chunk(indexed, unindexed) : Chunk(unindexed, indexed));
    }

    [Fact]
    public async Task Argument_byte_limit_applies_to_the_whole_call_across_chunks() =>
        await AssertRejectedAsync(Chunk(Call(0, "call-a", "files.read", new string('\u4e00', 12_000))),
            Chunk(Call(0, null, null, new string('\u4e00', 12_000))));

    [Fact]
    public async Task Done_marker_flushes_indexed_calls_without_a_finish_chunk()
    {
        var result = await RunAsync(false, Chunk(Call(0, "call-a", "files.read", "{\"path\":\"a.txt\"}")));
        Assert.Equal("a.txt", Assert.Single(result.ToolCalls).Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Usage_on_an_indexed_argument_chunk_survives_until_done()
    {
        var chunk = JsonNode.Parse(Chunk(Call(0, "call-a", "files.read", "{\"path\":\"a.txt\"}")))!.AsObject();
        chunk["usage"] = new JsonObject { ["prompt_tokens"] = 123, ["completion_tokens"] = 45, ["total_tokens"] = 168 };
        var result = await RunAsync(false, chunk.ToJsonString());
        Assert.Equal(45, result.Usage?.OutputTokens);
    }

    [Fact]
    public async Task Indexed_calls_are_discarded_when_the_provider_reports_length()
    {
        var result = await RunAsync(false,
            Chunk(Call(0, "call-a", "files.read", "{\"path\":\"a.txt\"}")),
            "{\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}");
        Assert.Equal("length", result.FinishReason);
        Assert.Empty(result.ToolCalls);
    }

    private static async Task AssertRejectedAsync(params string[] chunks)
    {
        var error = await Assert.ThrowsAsync<ModelStepException>(() => RunAsync(chunks));
        Assert.Equal("protocol_failure", error.Code);
        Assert.Equal("malformed_payload", Assert.IsType<ModelProviderException>(error.InnerException).Code);
    }

    private static JsonObject Call(int index, string? id, string? name, string arguments)
    {
        var function = new JsonObject { ["arguments"] = arguments };
        if (name is not null) function["name"] = name;
        var call = new JsonObject { ["index"] = index, ["function"] = function };
        if (id is not null) call["id"] = id;
        return call;
    }

    private static string Chunk(params JsonObject[] calls) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(calls.Cast<JsonNode?>().ToArray()) } }),
    }.ToJsonString();

    private static Task<ModelStepServiceResult> RunAsync(params string[] chunks) => RunAsync(true, chunks);

    private static async Task<ModelStepServiceResult> RunAsync(bool includeFinish, params string[] chunks)
    {
        var sse = string.Concat(chunks.Select(chunk => "data: " + chunk + "\n\n"))
            + (includeFinish ? "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" : "")
            + "data: [DONE]\n\n";
        using var client = new HttpClient(new Handler(sse));
        var request = new ModelGenerationRequest("inspect files", [], SystemPromptContext.Empty,
            new ModelGenerationSettings(new JsonArray(new JsonObject
            {
                ["tool_id"] = "files.read", ["tool_version"] = "1.0.0",
                ["input_schema"] = new JsonObject { ["type"] = "object" },
                ["scopes"] = new JsonArray("workspace.read"), ["availability"] = "executable",
                ["model_enabled"] = true, ["capability_class"] = "tool",
            })),
            new ProviderBootstrapData("fixture", ProviderProtocols.ChatCompletions,
                new Uri("https://provider.invalid/v1/chat/completions"), "fixture-model", "synthetic"));
        return await new ModelStepService(new OpenAiCompatibleModelProvider(client)).RunAsync(request, default);
    }

    private sealed class Handler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
    }
}
