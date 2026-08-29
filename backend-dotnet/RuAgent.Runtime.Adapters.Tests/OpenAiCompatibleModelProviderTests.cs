using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class OpenAiCompatibleModelProviderTests
{
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

    private static ModelRequest Request() => new(
        "test-model",
        "system prompt",
        "hello",
        Array.Empty<ModelTurn>(),
        new JsonObject { ["temperature"] = 0.7 },
        new Uri("https://provider.invalid/v1/chat/completions"),
        "provider-secret");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public HttpRequestMessage Request { get; private set; } = null!;

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
            return await _handler(request, cancellationToken);
        }
    }
}
