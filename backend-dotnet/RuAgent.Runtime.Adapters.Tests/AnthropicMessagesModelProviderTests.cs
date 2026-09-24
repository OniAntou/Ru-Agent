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
        Assert.NotNull(body["thinking"]);
        Assert.NotNull(body["output_config"]);
        Assert.NotNull(body["messages"]);
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
    public async Task GenerateAsync_rejects_max_tokens_above_anthropic_cap_as_malformed_payload()
    {
        // 0 is rejected at ModelRequest ctor (ArgumentException); provider caps at 8192, so 8193 -> malformed_payload
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullLifecycle(), Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8193), CancellationToken.None)));
        Assert.Equal("malformed_payload", ex.Code);
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
        var sse = MessageStart() + ContentBlockStart(0, "thinking") + ContentBlockDeltaThinking(0, "reasoning...") + ContentBlockStop(0) + ContentBlockStart(0, "text") + ContentBlockDeltaText(0, "answer") + ContentBlockStop(0) + MessageDelta() + MessageStop();
        // Note: second block uses same index after first closed — provider expects new blockIndex free, index 0 reused; still valid after stop
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

    [Fact]
    public async Task GenerateAsync_maps_overloaded_error_to_server_error()
    {
        var sse = MessageStart() + SseRaw("error", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("server_error", ex.Code);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task GenerateAsync_maps_generic_error_to_http_error()
    {
        var sse = MessageStart() + SseRaw("error", "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\"}}");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var provider = new AnthropicMessagesModelProvider(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await CollectAsync(provider.GenerateAsync(Request(null, 8192), CancellationToken.None)));
        Assert.Equal("http_error", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_failed", false)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
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

    private static ModelRequest Request(
        JsonObject? settings,
        int? maxTokens,
        IReadOnlyList<ModelTurn>? turns = null) => new(
        "test-model",
        "system prompt",
        "hello",
        turns ?? Array.Empty<ModelTurn>(),
        settings ?? new JsonObject(),
        new Uri("https://provider.invalid/v1/messages"),
        "synthetic-anthropic",
        maxTokens);

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
