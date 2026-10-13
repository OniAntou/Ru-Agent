using System.Text;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class BoundedJsonResponseReaderTests
{
    [Fact]
    public async Task Exact_bound_is_accepted_without_a_content_length()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"text":"Tiếng Việt"}""");
        using var content = new StreamContent(new NonSeekableStream(bytes));

        using var result = await BoundedJsonResponseReader.ReadAsync(content, bytes.Length, CancellationToken.None);

        Assert.Equal("Tiếng Việt", result.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Stream_overflow_is_rejected_even_when_content_length_understates_the_body()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"text":"oversized"}""");
        using var content = new StreamContent(new NonSeekableStream(bytes));
        content.Headers.ContentLength = 1;

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            BoundedJsonResponseReader.ReadAsync(content, bytes.Length - 1, CancellationToken.None));

        Assert.Equal("response_too_large", error.ErrorCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var content = new StringContent("{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedJsonResponseReader.ReadAsync(content, 100, cancellation.Token));
    }

    [Fact]
    public async Task Malformed_json_has_a_named_failure()
    {
        using var content = new StringContent("{");

        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            BoundedJsonResponseReader.ReadAsync(content, 100, CancellationToken.None));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
