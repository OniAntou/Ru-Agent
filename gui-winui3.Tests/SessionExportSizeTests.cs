using System.Net;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionExportSizeTests
{
    [Fact]
    public async Task Export_surfaces_the_hosts_named_size_refusal()
    {
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic-secret"), new RefusalHandler());

        var result = await api.ExportSessionAsync("s1", "claude-code");

        Assert.False(result.Ok);
        Assert.Empty(result.Paths);
        Assert.Contains("export_too_large", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_rejects_an_oversized_response_before_writing_files()
    {
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic-secret"), new OversizeHandler());

        var result = await api.ExportSessionAsync("s1", "claude-code");

        Assert.False(result.Ok);
        Assert.Empty(result.Paths);
        Assert.Contains("response_too_large", result.Error, StringComparison.Ordinal);
    }

    private sealed class OversizeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent("""{"status":"error","message":"This small body must not be parsed."}""");
            content.Headers.ContentLength = (128L * 1024 * 1024) + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class RefusalHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
            {
                Content = new StringContent("""{"status":"error","message":"export_too_large: export envelopes are limited to 128 MiB."}"""),
            });
    }
}
