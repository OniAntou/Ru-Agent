using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class OpenAiCompatibleModelProviderTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reasoning_effort\":\"high\"}")]
    [InlineData("{\"reasoning\":{\"effort\":\"high\",\"enabled\":true}}")]
    public async Task GenerateAsync_serializes_verified_reasoning_or_omits_default(string optionsJson)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var options = JsonNode.Parse(optionsJson)!.AsObject();
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        _ = await provider.GenerateAsync(Request(options), CancellationToken.None).ToListAsync();

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal("test-model", body["model"]!.GetValue<string>());
        Assert.Equal(options.ContainsKey("reasoning"), body.ContainsKey("reasoning"));
        Assert.Equal(options.ContainsKey("reasoning_effort"), body.ContainsKey("reasoning_effort"));
        foreach (var pair in options)
            Assert.True(JsonNode.DeepEquals(pair.Value, body[pair.Key]));
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GenerateAsync_streams_text_and_tool_fragments_without_leaking_them_as_text()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Xin chào\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"files.read\",\"arguments\":\"{\\\"path\\\":\\\"README.md\\\"\"}}]}}]}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.Equal("Xin chào", deltas[0].Text);
        Assert.Single(deltas[1].ToolFragments);
        Assert.Equal("files.read", deltas[1].ToolFragments[0].ToolName);
        Assert.Equal("{\"path\":\"README.md\"", deltas[1].ToolFragments[0].ArgumentsFragment);
        Assert.True(deltas[^1].Completed);
        Assert.DoesNotContain("README.md", deltas[1].Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Bearer provider-secret", handler.Request.Headers.GetValues("Authorization"));
    }

    [Fact]
    public async Task GenerateAsync_advertises_model_tools_as_provider_functions()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));
        var settings = new JsonObject
        {
            ["model_tools"] = new JsonArray(new JsonObject
            {
                ["tool_id"] = "write",
                ["tool_version"] = "1.0.0",
                ["scopes"] = new JsonArray("workspace.write"),
                ["availability"] = "executable",
                ["model_enabled"] = true,
                ["capability_class"] = "tool",
                ["verification_required"] = false,
                ["input_schema"] = JsonNode.Parse(
                    "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\"}}}"),
            }),
        };

        _ = await provider.GenerateAsync(Request(settings), CancellationToken.None).ToListAsync();

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var tool = Assert.Single(body["tools"]!.AsArray())!.AsObject();
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("write", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("object", tool["function"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.False(body.ContainsKey("model_tools"));
    }

    [Fact]
    public async Task GenerateAsync_preserves_reasoning_content_deltas()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Let me think\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"done\",\"finish_reason\":\"stop\"}}]}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.Equal("Let me think", deltas[0].Reasoning);
        Assert.Null(deltas[0].Text);
        Assert.Equal("done", deltas[1].Text);
    }

    [Fact]
    public async Task GenerateAsync_accepts_reasoning_field_as_thinking_fallback()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"reasoning\":\"Considering scope\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"done\",\"finish_reason\":\"stop\"}}]}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.Equal("Considering scope", deltas[0].Reasoning);
        Assert.Null(deltas[0].Text);
    }

    [Fact]
    public async Task GenerateAsync_skips_structured_reasoning_without_failing()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"reasoning\":[{\"type\":\"summary\"}],\"content\":\"done\",\"finish_reason\":\"stop\"}}]}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.Null(deltas[0].Reasoning);
        Assert.Equal("done", deltas[0].Text);
    }

    [Theory]
    [InlineData("{\"reasoning_effort\":\"high\"}", true)]
    [InlineData("{\"reasoning\":{\"effort\":\"high\"}}", true)]
    [InlineData("{}", false)]
    public async Task GenerateAsync_requests_reasoning_only_when_asked(string optionsJson, bool expected)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        _ = await provider.GenerateAsync(
            Request(JsonNode.Parse(optionsJson)!.AsObject()),
            CancellationToken.None).ToListAsync();

        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        Assert.Equal(expected, body.ContainsKey("include_reasoning"));
        if (expected)
        {
            Assert.True(body["include_reasoning"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task GenerateAsync_preserves_provider_usage_from_terminal_stream_chunk()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5,\"total_tokens\":17,\"prompt_tokens_details\":{\"cached_tokens\":3}}}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.NotNull(deltas[0].Usage);
        var usage = deltas[0].Usage!;
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(17, usage.TotalTokens);
        Assert.Equal(3, usage.CacheReadInputTokens);
    }

    [Fact]
    public async Task GenerateAsync_keeps_the_usage_tail_sent_after_the_finish_reason_chunk()
    {
        // include_usage places the usage report in a `choices: []` chunk that
        // arrives after finish_reason. Ending the stream on finish_reason used
        // to drop it, so every chat_completions run reported zero usage.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":21,\"completion_tokens\":7,\"total_tokens\":28}}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.Single(deltas, delta => delta.Completed);
        var tail = deltas[^1];
        Assert.False(tail.Completed);
        Assert.NotNull(tail.Usage);
        Assert.Equal(21, tail.Usage!.InputTokens);
        Assert.Equal(7, tail.Usage.OutputTokens);
    }

    [Fact]
    public async Task GenerateAsync_treats_null_prompt_tokens_details_as_absent()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5,\"total_tokens\":17,\"prompt_tokens_details\":null}}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var deltas = await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync();

        Assert.NotNull(deltas[0].Usage);
        var usage = deltas[0].Usage!;
        Assert.Equal(12, usage.InputTokens);
        Assert.Null(usage.CacheReadInputTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_failed", false)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
    [InlineData(HttpStatusCode.InternalServerError, "server_error", true)]
    public async Task GenerateAsync_maps_http_failures_to_bounded_codes(HttpStatusCode status, string code, bool retryable)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("provider secret body", Encoding.UTF8, "text/plain"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync());

        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain("provider secret body", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_rejects_malformed_or_missing_choices()
    {
        foreach (var body in new[] { "data: not-json\n\n", "data: {\"id\":\"missing-choices\"}\n\n" })
        {
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
            var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

            var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
                await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync());
            Assert.Equal("malformed_payload", error.Code);
        }
    }

    [Fact]
    public async Task GenerateAsync_rejects_a_single_oversized_sse_line_before_buffering_it()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: " + new string('x', 256 * 1024) + "\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync());

        Assert.Equal("response_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task GenerateAsync_maps_malformed_tool_fields_to_a_bounded_payload_error()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":42,\"function\":{\"name\":\"files.read\",\"arguments\":\"{}\"}}]}}]}\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await provider.GenerateAsync(Request(), CancellationToken.None).ToListAsync());

        Assert.Equal("malformed_payload", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task GenerateAsync_rejects_malformed_model_tools_instead_of_forwarding_them()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await provider.GenerateAsync(
                Request(new JsonObject
                {
                    ["temperature"] = 0.7,
                    ["model_tools"] = new JsonArray(new JsonObject { ["tool_id"] = "files.read" }),
                }),
                CancellationToken.None).ToListAsync());

        Assert.Equal("malformed_payload", error.Code);
        Assert.False(error.Retryable);
        // The Host/Worker selection contract itself never reaches the provider.
        Assert.DoesNotContain("model_tools", handler.RequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));
        var task = provider.GenerateAsync(Request(), cancellation.Token).ToListAsync().AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task OpenAiCompatible_with_image_includes_image_url()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));
        var req = new ModelRequest(
            "gpt-4o",
            "system prompt",
            "describe image",
            Array.Empty<ModelTurn>(),
            new JsonObject(),
            new Uri("https://provider.invalid/v1/chat/completions"),
            "provider-secret",
            100,
            new[] { new ModelImage("image/png", "iVBORw0KGgo=") });

        _ = await provider.GenerateAsync(req, CancellationToken.None).ToListAsync();
        var body = JsonNode.Parse(handler.RequestBody!)!.AsObject();
        var messages = body["messages"]!.AsArray();
        var userTurn = messages[^1]!.AsObject();
        var content = userTurn["content"]!.AsArray();
        var textPart = Assert.Single(content, n => n!["type"]!.GetValue<string>() == "text");
        Assert.Equal("describe image", textPart!["text"]!.GetValue<string>());
        var imgPart = Assert.Single(content, n => n!["type"]!.GetValue<string>() == "image_url");
        var imgObj = imgPart!["image_url"]!.AsObject();
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", imgObj["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAiCompatible_with_image_rejects_non_vision_model()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler));
        var req = new ModelRequest(
            "test-model",
            "system prompt",
            "describe image",
            Array.Empty<ModelTurn>(),
            new JsonObject(),
            new Uri("https://provider.invalid/v1/chat/completions"),
            "provider-secret",
            100,
            new[] { new ModelImage("image/png", "iVBORw0KGgo=") });

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await provider.GenerateAsync(req, CancellationToken.None).ToListAsync());
        Assert.Equal("vision_unsupported", ex.Code);
    }

    private static ModelRequest Request(JsonObject? settings = null) => new(
        "test-model",
        "system prompt",
        "hello",
        Array.Empty<ModelTurn>(),
        settings ?? new JsonObject { ["temperature"] = 0.7 },
        new Uri("https://provider.invalid/v1/chat/completions"),
        "provider-secret");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public HttpRequestMessage Request { get; private set; } = null!;
        public string? RequestBody { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return await _handler(request, cancellationToken);
        }
    }
}
