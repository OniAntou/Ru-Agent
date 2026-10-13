using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SessionExportSizeTests
{
    [Fact]
    public async Task Export_accepts_a_valid_envelope_above_the_general_response_limit()
    {
        var content = new string('x', 9 * 1024 * 1024);
        var body = JsonSerializer.Serialize(new
        {
            status = "ok", harness = "claude-code", session = new { id = "s1" },
            files = new[] { new { name = "session.jsonl", content } },
        });
        using var http = new HttpClient(new ReplyHandler(new StringContent(body, Encoding.UTF8, "application/json")));
        var client = new SessionInterchangeApiClient(Session(), http);

        var result = await client.ExportAsync("s1", "claude-code");

        Assert.Equal(content, Assert.Single(result.Files).Content);
    }

    [Fact]
    public async Task Export_reads_the_response_stream_without_eager_http_buffering()
    {
        using var http = new HttpClient(new ReplyHandler(new StreamOnlyContent()));
        var client = new SessionInterchangeApiClient(Session(), http);

        var result = await client.ExportAsync("s1", "claude-code");

        Assert.Equal("hello", Assert.Single(result.Files).Content);
    }

    [Fact]
    public async Task Export_refuses_a_declared_body_above_the_export_bound()
    {
        var content = new StringContent("{}");
        content.Headers.ContentLength = (128L * 1024 * 1024) + 1;
        using var http = new HttpClient(new ReplyHandler(content));
        var client = new SessionInterchangeApiClient(Session(), http);

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ExportAsync("s1", "claude-code"));

        Assert.Equal("response_too_large", error.ErrorCode);
    }

    private static PublicHostSession Session()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, "http://127.0.0.1:8000/",
            new string('e', 64), process.Id, process.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class ReplyHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class StreamOnlyContent : HttpContent
    {
        private static readonly byte[] Body = Encoding.UTF8.GetBytes(
            """{"status":"ok","harness":"claude-code","session":{"id":"s1"},"files":[{"name":"session.jsonl","content":"hello"}]}""");

        protected override bool TryComputeLength(out long length) { length = Body.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Export must not buffer the HTTP content before applying its bound.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(Body));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Body));
    }
}
