using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ModelHttpTransportBehaviorTests
{
    public static TheoryData<int> Protocols => new() { 0, 1, 2 };

    [Fact]
    public void Default_request_and_sse_idle_limits_are_both_two_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), ModelHttpTransport.DefaultRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), BoundedSseReader.DefaultIdleTimeout);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Request_deadline_bounds_stalled_headers_and_remains_retryable(int protocol)
    {
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        var provider = CreateProvider(protocol, client, TimeSpan.FromMilliseconds(50));

        var generation = CollectAsync(provider.GenerateAsync(Request(protocol), CancellationToken.None));
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => generation);

        Assert.Equal("timeout", error.Code);
        Assert.True(error.Retryable);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Caller_cancellation_during_request_stays_an_operation_cancellation(int protocol)
    {
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(protocol, client, TimeSpan.FromSeconds(5));
        var generation = CollectAsync(provider.GenerateAsync(Request(protocol), cancellation.Token));
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Provider_redirect_response_is_named_and_non_retryable(int protocol)
    {
        using var client = new HttpClient(new RedirectHandler());
        var provider = CreateProvider(protocol, client, TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CollectAsync(provider.GenerateAsync(Request(protocol), CancellationToken.None)));

        Assert.Equal("redirect_rejected", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Request_deadline_does_not_cancel_a_stream_after_response_headers()
    {
        using var client = new HttpClient(new StreamingHandler());
        var provider = CreateProvider(0, client, TimeSpan.FromMilliseconds(20));

        var deltas = await CollectAsync(provider.GenerateAsync(Request(0), CancellationToken.None));

        var terminal = Assert.Single(deltas);
        Assert.True(terminal.Completed);
        Assert.Equal("stop", terminal.FinishReason);
        Assert.Empty(terminal.ToolFragments);
    }

    private static IModelProvider CreateProvider(int protocol, HttpClient client, TimeSpan timeout) => protocol switch
    {
        0 => new OpenAiCompatibleModelProvider(client, timeout),
        1 => new OpenAiResponsesModelProvider(client, timeout),
        2 => new AnthropicMessagesModelProvider(client, timeout),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    private static ModelRequest Request(int protocol) => new(
        "test-model", "system prompt", "hello", [], new JsonObject(),
        protocol switch
        {
            0 => new Uri("https://provider.invalid/v1/chat/completions"),
            1 => new Uri("https://provider.invalid/v1/responses"),
            2 => new Uri("https://provider.invalid/v1/messages"),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        },
        "synthetic-credential",
        maximumOutputTokens: protocol == 2 ? 1024 : null);

    private static async Task<List<ModelDelta>> CollectAsync(IAsyncEnumerable<ModelDelta> source)
    {
        var deltas = new List<ModelDelta>();
        await foreach (var delta in source) deltas.Add(delta);
        return deltas;
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://redirected.invalid/collect") },
            });
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A cancelled request cannot return a response.");
        }
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DelayedReadStream(
                    Encoding.UTF8.GetBytes("data: [DONE]\n\n"), TimeSpan.FromMilliseconds(100))),
            });
    }

    private sealed class DelayedReadStream(byte[] bytes, TimeSpan delay) : MemoryStream(bytes)
    {
        private bool _delayed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(delay, cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
