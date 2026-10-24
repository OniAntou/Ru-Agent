using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class AnthropicMessagesModelProviderTests
{
    private static string Sse(string evt, JsonObject data) => $"event: {evt}\ndata: {data.ToJsonString()}\n\n";
    private static string SseRaw(string evt, string json) => $"event: {evt}\ndata: {json}\n\n";

    private static async Task<List<ModelDelta>> CollectAsync(IAsyncEnumerable<ModelDelta> source)
    {
        var list = new List<ModelDelta>();
        await foreach (var d in source) list.Add(d);
        return list;
    }

    // helpers to build Anthropic SSE fixtures
    private static string MessageStart(string model = "test-model") =>
        Sse("message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["model"] = model, ["content"] = new JsonArray() } });
    private static string ContentBlockStart(int idx = 0, string type = "text") =>
        Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = idx, ["content_block"] = new JsonObject { ["type"] = type, ["text"] = "" } });
    private static string ContentBlockDeltaText(int idx, string text) =>
        Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = idx, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } });
    private static string ContentBlockDeltaThinking(int idx, string thinking = "hmm") =>
        Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = idx, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking } });
    private static string ContentBlockStop(int idx = 0) =>
        Sse("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = idx });
    private static string MessageDelta(string reason = "end_turn") =>
        Sse("message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = reason } });
    private static string MessageStop() =>
        Sse("message_stop", new JsonObject { ["type"] = "message_stop" });
    private static string Ping() => "event: ping\ndata:\n\n";

    private static string FullLifecycle(string stopReason = "end_turn", string model = "test-model")
    {
        return MessageStart(model) + ContentBlockStart(0, "text") + ContentBlockDeltaText(0, "Hello ") + ContentBlockDeltaText(0, "world") + ContentBlockStop(0) + MessageDelta(stopReason) + MessageStop();
    }

    // ---------- request tests ----------

    [Fact]
    public void ProjectRequestBody_accepts_the_entire_catalog_and_rejects_an_extra_definition()
    {
        var tools = new JsonArray(RuAgent.Protocol.AgentToolDefinitionCatalog.Get()
            .Select(d => (JsonNode)new JsonObject
            { ["tool_id"] = d.ToolId, ["input_schema"] = d.InputSchema }).ToArray());
        var provider = new AnthropicMessagesModelProvider(new HttpClient());
        var settings = new JsonObject { ["model_tools"] = tools };
        var body = JsonNode.Parse(provider.ProjectRequestBody(
            Request(settings, 8192, nativeToolCalling: true)))!.AsObject();
        Assert.Equal(tools.Count, body["tools"]!.AsArray().Count);
        tools.Add(tools[0]!.DeepClone());
        var error = Assert.Throws<ModelProviderException>(() => provider.ProjectRequestBody(
            Request(settings, 8192, nativeToolCalling: true)));
        Assert.Equal("malformed_payload", error.Code);
    }

    [Fact]
    public async Task GenerateAsync_sends_anthropic_json_fields_with_thinking()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var settings = new JsonObject
        {
            ["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = 1000 },
            ["output_config"] = new JsonObject { ["effort"] = "high" }
        };
        _ = await CollectAsync(provider.GenerateAsync(Request(settings, 8192), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("test-model", body["model"]!.GetValue<string>());
        Assert.Equal("system prompt", body["system"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(8192, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("tools"));
        Assert.NotNull(body["thinking"]);
        Assert.NotNull(body["output_config"]);
        Assert.NotNull(body["messages"]);
        Assert.False(body.ContainsKey("cache_control"));
    }

    [Fact]
    public void ProjectRequestBody_asks_the_first_party_endpoint_to_cache_the_prompt()
    {
        var provider = new AnthropicMessagesModelProvider(new HttpClient());
        var body = JsonNode.Parse(provider.ProjectRequestBody(
            Request(null, 8192, endpoint: new Uri("https://api.anthropic.com/v1/messages"))))!.AsObject();

        Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
        Assert.Single(body["cache_control"]!.AsObject());
    }

    [Theory]
    [InlineData("https://gateway.example.com/anthropic/v1/messages")]
    [InlineData("http://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com.example.com/v1/messages")]
    public void ProjectRequestBody_leaves_compatible_endpoints_uncached(string endpoint)
    {
        var provider = new AnthropicMessagesModelProvider(new HttpClient());
        var body = JsonNode.Parse(provider.ProjectRequestBody(
            Request(null, 8192, endpoint: new Uri(endpoint))))!.AsObject();

        Assert.False(body.ContainsKey("cache_control"));
    }

    [Fact]
    public async Task GenerateAsync_native_tool_use_emits_declarations_and_retains_signed_blocks_privately()
    {
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } })
            + ContentBlockDeltaText(0, "Reading the file.") + ContentBlockStop(0)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" } })
            + ContentBlockDeltaThinking(1, "private thought")
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "opaque-" } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "signature" } })
            + ContentBlockStop(1)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 2, ["content_block"] = new JsonObject { ["type"] = "redacted_thinking", ["data"] = "opaque-redacted-bytes" } })
            + ContentBlockStop(2)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 3, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_read_1", ["name"] = "read", ["input"] = new JsonObject() } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 3, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"path\":" } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 3, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "\"seed.txt\"}" } })
            + ContentBlockStop(3) + MessageDelta("tool_use") + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var settings = NativeToolSettings();

        var deltas = await CollectAsync(provider.GenerateAsync(Request(settings, 8192, nativeToolCalling: true), CancellationToken.None));

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("read", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, body["tools"]![0]!["input_schema"]!.GetValueKind());
        Assert.Equal("Reading the file.", string.Concat(deltas.Where(delta => delta.Text is not null).Select(delta => delta.Text)));
        Assert.DoesNotContain(deltas, delta => delta.Reasoning is not null);
        var call = Assert.Single(deltas.SelectMany(delta => delta.ToolFragments));
        Assert.Equal("toolu_read_1", call.ProviderCallId);
        Assert.Equal("read", call.ToolName);
        Assert.Equal("{\"path\":\"seed.txt\"}", call.ArgumentsFragment);
        var continuation = Assert.IsType<RuAgent.Harness.Core.Prompting.ProviderContinuation>(deltas[^1].ProviderContinuation);
        Assert.Equal("anthropic_messages", continuation.Protocol);
        Assert.Equal(["text", "thinking", "redacted_thinking", "tool_use"], continuation.OutputItems.Select(item => item["type"]!.GetValue<string>()));
        Assert.Equal("private thought", continuation.OutputItems[1]["thinking"]!.GetValue<string>());
        Assert.Equal("opaque-signature", continuation.OutputItems[1]["signature"]!.GetValue<string>());
        Assert.Equal("opaque-redacted-bytes", continuation.OutputItems[2]["data"]!.GetValue<string>());
        Assert.True(deltas[^1].Completed);
    }

    [Fact]
    public async Task GenerateAsync_native_history_replays_exact_assistant_blocks_and_error_result_ids()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream"),
        });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var messages = new[]
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "I will inspect." },
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "private", ["signature"] = "opaque-signature" },
                    new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_read_1", ["name"] = "read", ["input"] = JsonNode.Parse("{\"path\":\"seed.txt\"}") }),
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result", ["tool_use_id"] = "toolu_read_1", ["content"] = "denied", ["is_error"] = true,
                }),
            },
        };
        var request = new ModelRequest("test-model", "system prompt", "continue", Array.Empty<ModelTurn>(),
            NativeToolSettings(), new Uri("https://provider.invalid/v1/messages"), "synthetic-anthropic", 8192,
            nativeToolCalling: true, anthropicMessages: messages);

        _ = await CollectAsync(provider.GenerateAsync(request, CancellationToken.None));

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var replayed = body["messages"]!.AsArray();
        Assert.Equal("opaque-signature", replayed[0]!["content"]![1]!["signature"]!.GetValue<string>());
        Assert.Equal("toolu_read_1", replayed[0]!["content"]![2]!["id"]!.GetValue<string>());
        Assert.Equal("toolu_read_1", replayed[1]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
        Assert.True(replayed[1]!["content"]![0]!["is_error"]!.GetValue<bool>());
        Assert.Equal("continue", replayed[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateAsync_native_stream_rejects_duplicate_tool_use_identity()
    {
        var toolBlock = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_duplicate", ["name"] = "read", ["input"] = new JsonObject() };
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = (JsonObject)toolBlock.DeepClone() })
            + ContentBlockStop(0)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = (JsonObject)toolBlock.DeepClone() });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(
            provider.GenerateAsync(Request(NativeToolSettings(), 8192, nativeToolCalling: true), CancellationToken.None)));

        Assert.Equal("malformed_payload", failure.Code);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(0, 2)]
    public async Task GenerateAsync_native_stream_rejects_nonsequential_content_indexes(int firstIndex, int secondIndex)
    {
        static string ToolBlock(int index, string id) =>
            Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "read", ["input"] = new JsonObject() } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"path\":\"seed.txt\"}" } })
            + ContentBlockStop(index);
        var lifecycle = MessageStart() + ToolBlock(firstIndex, "toolu_first") + ToolBlock(secondIndex, "toolu_second")
            + MessageDelta("tool_use") + MessageStop();
        var provider = new AnthropicMessagesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") })));
        var deltas = new List<ModelDelta>();

        var failure = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var delta in provider.GenerateAsync(Request(NativeToolSettings(), 8192, nativeToolCalling: true), CancellationToken.None))
                deltas.Add(delta);
        });

        Assert.Equal("malformed_payload", failure.Code);
        Assert.DoesNotContain(deltas, delta => delta.Completed);
        Assert.DoesNotContain(deltas, delta => delta.ProviderContinuation is not null);
    }

    [Fact]
    public async Task GenerateAsync_native_stream_rejects_thinking_after_signature_starts()
    {
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" } })
            + ContentBlockDeltaThinking(0, "signed content")
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "" } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "part-one" } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "part-two" } })
            + ContentBlockDeltaThinking(0, " changed")
            + ContentBlockStop(0) + MessageDelta("end_turn") + MessageStop();
        var provider = new AnthropicMessagesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") })));

        var failure = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(
            provider.GenerateAsync(Request(NativeToolSettings(), 8192, nativeToolCalling: true), CancellationToken.None)));

        Assert.Equal("malformed_payload", failure.Code);
    }

    [Fact]
    public async Task GenerateAsync_native_partial_tool_stream_never_marks_a_completed_step()
    {
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_partial", ["name"] = "read", ["input"] = new JsonObject() } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"path\":\"seed.txt\"}" } })
            + ContentBlockStop(0);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = new List<ModelDelta>();

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var delta in provider.GenerateAsync(Request(NativeToolSettings(), 8192, nativeToolCalling: true), CancellationToken.None))
                deltas.Add(delta);
        });

        Assert.Contains(deltas, delta => delta.ToolFragments.Count == 1);
        Assert.DoesNotContain(deltas, delta => delta.Completed);
        Assert.DoesNotContain(deltas, delta => delta.ProviderContinuation is not null);
    }

    [Fact]
    public async Task GenerateAsync_legacy_mode_rejects_native_redacted_thinking_blocks()
    {
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "redacted_thinking", ["data"] = "opaque" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(
            provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));

        Assert.Equal("malformed_payload", failure.Code);
        Assert.DoesNotContain("tools", JsonNode.Parse(handler.RequestBody!)!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task GenerateAsync_native_continuation_preserves_text_present_in_block_start()
    {
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "starting narration" } })
            + ContentBlockStop(0)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_start", ["name"] = "read", ["input"] = new JsonObject() } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"path\":\"seed.txt\"}" } })
            + ContentBlockStop(1) + MessageDelta("tool_use") + MessageStop();
        var provider = new AnthropicMessagesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") })));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(NativeToolSettings(), 8192, nativeToolCalling: true), CancellationToken.None));

        Assert.Equal("starting narration", Assert.Single(deltas, delta => delta.Text is not null).Text);
        Assert.Equal("starting narration", deltas[^1].ProviderContinuation!.OutputItems[0]["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateAsync_native_aggregate_continuation_overflow_is_a_bounded_provider_error()
    {
        const int blockLength = 64_000;
        var text = new string('x', blockLength);
        var lifecycle = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = text } })
            + ContentBlockStop(0)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = text } })
            + ContentBlockStop(1)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 2, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = text } })
            + ContentBlockStop(2)
            + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 3, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_overflow", ["name"] = "read", ["input"] = new JsonObject() } })
            + Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = 3, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"path\":\"seed.txt\"}" } })
            + ContentBlockStop(3) + MessageDelta("tool_use") + MessageStop();
        var provider = new AnthropicMessagesModelProvider(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream") })));

        var failure = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(
            provider.GenerateAsync(Request(NativeToolSettings(), 8192, nativeToolCalling: true), CancellationToken.None)));

        Assert.Equal("response_too_large", failure.Code);
    }

    [Fact]
    public async Task GenerateAsync_omits_thinking_and_output_config_when_provider_default()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        _ = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.False(body.ContainsKey("thinking"));
        Assert.False(body.ContainsKey("output_config"));
        Assert.True(body.ContainsKey("max_tokens"));
    }

    [Fact]
    public async Task GenerateAsync_sends_output_caps_up_to_the_adapter_limit()
    {
        // The adapter once capped Anthropic at 8,192, which squeezed thinking and failed
        // large writes. It now sends up to the shared 128,000 request bound; a larger value
        // never reaches it because ModelRequest rejects it at construction.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        _ = await CollectAsync(provider.GenerateAsync(Request(null, AnthropicMessagesModelProvider.MaximumSupportedOutputTokens), CancellationToken.None));
        Assert.Equal(AnthropicMessagesModelProvider.MaximumSupportedOutputTokens, JsonNode.Parse(handler.RequestBody!)!["max_tokens"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => Request(null, AnthropicMessagesModelProvider.MaximumSupportedOutputTokens + 1));
    }

    [Fact]
    public async Task ModelRequest_rejects_zero_max_tokens_at_construction()
    {
        Assert.Throws<ArgumentException>(() => Request(null, 0));
    }

    [Fact]
    public async Task GenerateAsync_rejects_null_max_tokens_as_malformed_payload()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var req = new ModelRequest("test-model", "system prompt", "hello", Array.Empty<ModelTurn>(), new JsonObject(), new Uri("https://provider.invalid/v1/messages"), "synthetic-anthropic", null);
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(req, CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_sets_only_anthropic_headers()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        _ = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.True(handler.Request.Headers.Contains("x-api-key"));
        Assert.Equal("synthetic-anthropic", handler.Request.Headers.GetValues("x-api-key").First());
        Assert.True(handler.Request.Headers.Contains("anthropic-version"));
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").First());
        Assert.False(handler.Request.Headers.Contains("Authorization"));
        Assert.DoesNotContain("Bearer", handler.Request.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
    }

    // ---------- stream tests ----------

    [Fact]
    public async Task GenerateAsync_streams_full_lifecycle_text()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.Equal("Hello ", deltas[0].Text);
        Assert.Equal("world", deltas[1].Text);
        Assert.True(deltas[^1].Completed);
        Assert.Equal("stop", deltas[^1].FinishReason);
    }

    [Fact]
    public async Task GenerateAsync_preserves_usage_from_message_delta()
    {
        var messageStart = Sse("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["model"] = "test-model",
                ["content"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 12,
                    ["cache_read_input_tokens"] = 3
                }
            }
        });
        var usageDelta = Sse("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" },
            ["usage"] = new JsonObject
            {
                ["output_tokens"] = 5,
                ["cache_creation_input_tokens"] = 2
            }
        });
        var lifecycle = messageStart + ContentBlockStart() + ContentBlockDeltaText(0, "ok") +
            ContentBlockStop() + usageDelta + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(lifecycle, Encoding.UTF8, "text/event-stream")
        });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));

        Assert.NotNull(deltas[^1].Usage);
        var usage = deltas[^1].Usage!;
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(3, usage.CacheReadInputTokens);
        Assert.Equal(2, usage.CacheWriteInputTokens);
    }

    [Fact]
    public async Task GenerateAsync_ignores_ping()
    {
        var sse = MessageStart() + Ping() + ContentBlockStart() + ContentBlockDeltaText(0, "hi") + ContentBlockStop() + MessageDelta() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.Contains(deltas, d => d.Text == "hi");
        Assert.True(deltas[^1].Completed);
    }

    [Fact]
    public async Task GenerateAsync_surfaces_thinking_delta_as_reasoning()
    {
        var sse = MessageStart() + ContentBlockStart(0, "thinking") + ContentBlockDeltaThinking(0, "reasoning...") + ContentBlockStop(0) + ContentBlockStart(1, "text") + ContentBlockDeltaText(1, "answer") + ContentBlockStop(1) + MessageDelta() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.Single(deltas, d => d.Text is not null);
        Assert.Equal("answer", deltas.First(d => d.Text is not null).Text);
        // Thinking rides the reasoning channel for the activity feed, never as chat text.
        Assert.Contains(deltas, d => d.Reasoning == "reasoning...");
        Assert.DoesNotContain(deltas, d => (d.Text ?? string.Empty).Contains("reasoning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_ignores_unknown_future_event()
    {
        var sse = MessageStart() + Sse("unknown_event", new JsonObject { ["type"] = "unknown_event" }) + ContentBlockStart() + ContentBlockDeltaText(0, "hi") + ContentBlockStop() + MessageDelta() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.Contains(deltas, d => d.Text == "hi");
        Assert.True(deltas[^1].Completed);
    }

    [Fact]
    public async Task GenerateAsync_rejects_known_event_with_unknown_json_type()
    {
        var sse = MessageStart()
            + Sse("content_block_start", new JsonObject { ["type"] = "future_event" })
            + ContentBlockStart() + ContentBlockDeltaText(0, "hi") + ContentBlockStop() + MessageDelta() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_maps_wrong_json_value_type_to_malformed_payload()
    {
        var sse = Sse("message_start", new JsonObject { ["type"] = 42 });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_ping_type_under_known_non_ping_event()
    {
        var sse = Sse("message_start", new JsonObject { ["type"] = "ping" }) + FullLifecycle();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_maps_scalar_message_container_to_malformed_payload()
    {
        var sse = Sse("message_start", new JsonObject { ["type"] = "message_start", ["message"] = 42 });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_ignores_ping_with_json_data()
    {
        var sse = MessageStart() + Sse("ping", new JsonObject { ["type"] = "ping" }) + ContentBlockStart() + ContentBlockDeltaText(0, "hi") + ContentBlockStop() + MessageDelta() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.Contains(deltas, d => d.Text == "hi");
    }

    [Fact]
    public async Task GenerateAsync_maps_max_tokens_to_length()
    {
        var sse = MessageStart() + ContentBlockStart() + ContentBlockDeltaText(0, "hi") + ContentBlockStop() + MessageDelta("max_tokens") + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));
        Assert.Equal("length", deltas[^1].FinishReason);
    }

    [Theory]
    [InlineData("overloaded_error", "server_error", true)]
    [InlineData("rate_limit_error", "rate_limited", true)]
    [InlineData("api_error", "server_error", true)]
    [InlineData("invalid_request_error", "http_error", false)]
    public async Task GenerateAsync_maps_stream_error_events_like_http_failures(
        string errorType,
        string code,
        bool retryable)
    {
        var sse = MessageStart() + SseRaw("error", $"{{\"type\":\"error\",\"error\":{{\"type\":\"{errorType}\"}}}}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_failed", false)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
    [InlineData(HttpStatusCode.NotFound, "model_unavailable", false)]
    [InlineData(HttpStatusCode.PaymentRequired, "provider_payment_required", false)]
    [InlineData((HttpStatusCode)529, "server_error", true)]
    [InlineData(HttpStatusCode.InternalServerError, "server_error", true)]
    public async Task GenerateAsync_maps_http_failures(HttpStatusCode status, string code, bool retryable)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("secret body", Encoding.UTF8, "text/plain") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain("secret body", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GenerateAsync_preserves_bounded_retry_after_for_retryable_http_response(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(status)
                { Content = new StringContent("secret body", Encoding.UTF8, "text/plain") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return response;
        });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal(TimeSpan.FromSeconds(9), error.RetryAfter);
        Assert.DoesNotContain("secret body", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_rejects_malformed_order_message_delta_before_start()
    {
        var sse = MessageDelta() + MessageStart() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_incomplete_EOF()
    {
        var sse = MessageStart() + ContentBlockStart() + ContentBlockDeltaText(0, "hi");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_observes_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var task = CollectAsync(provider.GenerateAsync(Request(null, 8192), cts.Token));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task GenerateAsync_rejects_fallback_block()
    {
        var startWithFallback = Sse("message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["model"] = "test-model", ["content"] = new JsonArray(new JsonObject { ["type"] = "fallback" }) } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(startWithFallback, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_model_mismatch()
    {
        var sse = MessageStart("other-model");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_rejects_tool_use_block()
    {
        var sse = MessageStart() + Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "x" } });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    public async Task GenerateAsync_rejects_turn_roles_not_supported_by_messages(string role)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not run"));
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        IReadOnlyList<ModelTurn> turns = role == "tool"
            ? new[]
            {
                new ModelTurn(
                    "assistant",
                    string.Empty,
                    [new ModelFunctionCall("call_1", "read", System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())]),
                new ModelTurn("tool", "hidden", toolCallId: "call_1", toolResultCode: "ok"),
            }
            : new[] { new ModelTurn(role, "hidden") };
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(
                Request(null, 8192, turns),
                CancellationToken.None)));

        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task AnthropicMessages_with_image_includes_image_block()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(model: "claude-3-5-sonnet"), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var req = new ModelRequest(
            "claude-3-5-sonnet",
            "system prompt",
            "describe image",
            Array.Empty<ModelTurn>(),
            new JsonObject(),
            new Uri("https://provider.invalid/v1/messages"),
            "synthetic-anthropic",
            8192,
            new[] { new ModelImage("image/png", "iVBORw0KGgo=") });

        _ = await CollectAsync(provider.GenerateAsync(req, CancellationToken.None));
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var messages = body["messages"]!.AsArray();
        var userTurn = messages[^1]!.AsObject();
        var content = userTurn["content"]!.AsArray();
        var imgBlock = Assert.Single(content, n => n!["type"]!.GetValue<string>() == "image");
        var source = imgBlock!["source"]!.AsObject();
        Assert.Equal("base64", source["type"]!.GetValue<string>());
        Assert.Equal("image/png", source["media_type"]!.GetValue<string>());
        Assert.Equal("iVBORw0KGgo=", source["data"]!.GetValue<string>());
        var textBlock = Assert.Single(content, n => n!["type"]!.GetValue<string>() == "text");
        Assert.Equal("describe image", textBlock!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnthropicMessages_with_image_rejects_non_vision_model()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var req = new ModelRequest(
            "test-model",
            "system prompt",
            "describe image",
            Array.Empty<ModelTurn>(),
            new JsonObject(),
            new Uri("https://provider.invalid/v1/messages"),
            "synthetic-anthropic",
            8192,
            new[] { new ModelImage("image/png", "iVBORw0KGgo=") });

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(req, CancellationToken.None)));
        Assert.Equal("vision_unsupported", ex.Code);
    }

    [Fact]
    public void ProjectRequestBody_omits_tools_when_the_tool_list_is_empty()
    {
        var provider = new AnthropicMessagesModelProvider(new HttpClient());
        var body = JsonNode.Parse(provider.ProjectRequestBody(
            Request(new JsonObject { ["model_tools"] = new JsonArray() }, 8192, nativeToolCalling: true)))!.AsObject();

        Assert.False(body.ContainsKey("tools"));
    }

    [Fact]
    public async Task GenerateAsync_accepts_a_content_block_with_an_extra_sibling_field()
    {
        // Anthropic may add fields to a valid block (citations on text blocks).
        // The required fields are validated; the extra ones are ignored instead
        // of failing a step whose block content is perfectly readable.
        var start = Sse("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = 0,
            ["content_block"] = new JsonObject
            {
                ["type"] = "text",
                ["text"] = "",
                ["citations"] = new JsonArray(),
            },
        });
        var sse = MessageStart() + start + ContentBlockDeltaText(0, "hi") + ContentBlockStop() + MessageDelta() + MessageStop();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None));

        Assert.Contains(deltas, d => d.Text == "hi");
    }

    private static JsonObject NativeToolSettings() => new()
    {
        ["model_tools"] = new JsonArray(new JsonObject
        {
            ["tool_id"] = "read", ["tool_version"] = "1", ["description"] = "Read a file",
            ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("path"), ["additionalProperties"] = false },
        }),
    };

    private static ModelRequest Request(
        JsonObject? settings,
        int? maxTokens,
        IReadOnlyList<ModelTurn>? turns = null,
        bool nativeToolCalling = false,
        Uri? endpoint = null) => new(
        "test-model",
        "system prompt",
        "hello",
        turns ?? Array.Empty<ModelTurn>(),
        settings ?? new JsonObject(),
        endpoint ?? new Uri("https://provider.invalid/v1/messages"),
        "synthetic-anthropic",
        maxTokens,
        nativeToolCalling: nativeToolCalling);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public HttpRequestMessage Request { get; private set; } = null!;
        public string? RequestBody { get; private set; }
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : this((r, _) => Task.FromResult(handler(r))) { }
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null) RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _handler(request, cancellationToken);
        }
    }
}
