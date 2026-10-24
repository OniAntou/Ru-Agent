using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ProviderQuotaTests
{
    [Theory]
    [InlineData(false, "error")]
    [InlineData(true, "error")]
    [InlineData(true, "response.failed")]
    public async Task Streamed_quota_errors_keep_the_same_nonretryable_code(bool responses, string eventType)
    {
        var error = new JsonObject { ["code"] = "insufficient_quota", ["message"] = "synthetic-private-body" };
        var body = eventType == "response.failed"
            ? new JsonObject { ["type"] = eventType, ["response"] = new JsonObject { ["error"] = error } }
            : new JsonObject { ["type"] = eventType, ["error"] = error };
        var content = new StringContent($"event: {eventType}\ndata: {body.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream");
        var failure = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses, content, status: HttpStatusCode.OK));
        Assert.Equal("quota_exhausted", failure.Code);
        Assert.False(failure.Retryable);
        Assert.DoesNotContain("synthetic-private-body", failure.Message);
    }

    [Theory]
    [InlineData(false, "insufficient_quota")]
    [InlineData(true, "insufficient_quota")]
    [InlineData(false, "credit_balance_exhausted")]
    [InlineData(true, "credit_balance_exhausted")]
    [InlineData(false, "organization_spend_limit_exceeded")]
    [InlineData(true, "project_spend_limit_exceeded")]
    [InlineData(false, "organization_usage_limit_exceeded")]
    [InlineData(true, "billing_hard_limit_reached")]
    public async Task Explicit_quota_codes_are_not_automatically_retryable(bool responses, string code)
    {
        var body = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "synthetic-private-body" } }.ToJsonString();
        var error = await Failure(responses, new StringContent(body));
        Assert.Equal("quota_exhausted", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("synthetic-private-body", error.Message);
    }

    [Theory]
    [InlineData("{\"error\":{\"type\":\"insufficient_quota\"}}", "quota_exhausted")]
    [InlineData("{\"error\":{\"type\":\"insufficient_quota\",\"code\":\"slow_down\"}}", "rate_limited")]
    [InlineData("{\"error\":{\"code\":123}}", "rate_limited")]
    [InlineData("{\"error\":{\"code\":\"unknown\"}}", "rate_limited")]
    [InlineData("not JSON: account may be empty", "rate_limited")]
    public async Task Unproven_limits_remain_throttles_even_with_zero_remaining_requests(string body, string expected)
    {
        var error = await Failure(false, new StringContent(body), remaining: "0");
        Assert.Equal(expected, error.Code);
        Assert.Equal(expected == "rate_limited", error.Retryable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_unavailability_also_preserves_retry_after(bool responses)
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses,
            new StringContent("synthetic private error"), status: HttpStatusCode.ServiceUnavailable,
            retryAfter: TimeSpan.FromSeconds(17)));
        Assert.Equal("server_error", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(17), error.RetryAfter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_retry_after_is_preserved_for_runner_admission(bool responses)
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses,
            new StringContent("{}"), retryAfter: TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
    }

    [Fact]
    public async Task Oversized_error_content_is_not_read_or_reported_as_quota()
    {
        var body = "{\"error\":{\"code\":\"insufficient_quota\"}}" + new string(' ', 20_000);
        var error = await Failure(false, new StringContent(body));
        Assert.Equal("rate_limited", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_after_delta_is_preserved_as_bounded_metadata(bool responses)
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses, new StringContent("{}"),
            retryAfter: TimeSpan.FromSeconds(17)));
        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(TimeSpan.FromSeconds(17), error.RetryAfter);
    }

    [Fact]
    public async Task Stalled_error_body_has_a_deadline_and_preserves_parent_cancellation()
    {
        var error = await Failure(false, new StreamContent(new HeldStream()), token: CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("rate_limited", error.Code);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Generate(false, new StreamContent(new HeldStream()), token: cancellation.Token));
    }

    private static async Task<ModelProviderException> Failure(bool responses, HttpContent content,
        string? remaining = null, CancellationToken token = default)
        => await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses, content, remaining, token));

    private static async Task Generate(bool responses, HttpContent content,
        string? remaining = null, CancellationToken token = default, HttpStatusCode status = HttpStatusCode.TooManyRequests,
        TimeSpan? retryAfter = null)
    {
        using var http = new HttpClient(new Handler(() =>
        {
            var response = new HttpResponseMessage(status) { Content = content };
            if (remaining is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", remaining);
            if (retryAfter is { } delay) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            return response;
        }));
        IModelProvider provider = responses ? new OpenAiResponsesModelProvider(http) : new OpenAiCompatibleModelProvider(http);
        var request = new ModelRequest("test-model", "system", "hello", [], new JsonObject(),
            new Uri("https://provider.invalid/v1/" + (responses ? "responses" : "chat/completions")), "synthetic", 8192);
        await foreach (var _ in provider.GenerateAsync(request, token)) { }
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(response());
    }

    private sealed class HeldStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
}
