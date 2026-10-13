using System.Text.Json;
using RuBackend.Business;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionExportEnvelopeLimitTests
{
    [Fact]
    public async Task Measures_encoded_bytes_including_nested_json_escaping()
    {
        var envelope = new { status = "ok", files = new[] { new { name = "session.jsonl", content = "\"<Tiếng Việt>\"\n" } } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerializerOptions.Web);

        Assert.True(await SessionExportEnvelopeLimit.FitsAsync(envelope, CancellationToken.None, bytes.Length));
        Assert.False(await SessionExportEnvelopeLimit.FitsAsync(envelope, CancellationToken.None, bytes.Length - 1));
    }

    [Fact]
    public async Task Measurement_preserves_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SessionExportEnvelopeLimit.FitsAsync(new { content = "hello" }, cancellation.Token));
    }
}
