using System.Net;
using System.Text;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The import API's error surface: the host's own bounded words reach the caller
/// instead of a generic status line, so a refusal (a foreign file, an oversized body)
/// reads like something a person can act on.
/// </summary>
public sealed class SessionImportApiTests
{
    private static ApiService CreateApi(HttpStatusCode status, string body) =>
        new(new BackendSession(new Uri("http://127.0.0.1:59999/"), "test-secret"), new StubHandler(status, body));

    [Fact]
    public async Task ImportSessionAsync_surfaces_the_hosts_413_message()
    {
        using var api = CreateApi(
            HttpStatusCode.RequestEntityTooLarge,
            "{\"status\":\"error\",\"message\":\"import_too_large: session files are limited to 16 MiB.\"}");

        var outcome = await api.ImportSessionAsync("claude-code", "{}");

        Assert.False(outcome.Ok);
        Assert.Contains("import_too_large", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportSessionAsync_surfaces_a_bounded_validation_refusal()
    {
        using var api = CreateApi(
            HttpStatusCode.OK,
            "{\"status\":\"error\",\"message\":\"This payload is not JSON, so it is not an opencode session.\"}");

        var outcome = await api.ImportSessionAsync("opencode", "not-json");

        Assert.False(outcome.Ok);
        Assert.Contains("not an opencode session", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportSessionAsync_falls_back_to_the_status_when_the_body_has_no_message()
    {
        using var api = CreateApi(HttpStatusCode.BadGateway, "not json");

        var outcome = await api.ImportSessionAsync("claude-code", "{}");

        Assert.False(outcome.Ok);
        Assert.Equal("HTTP 502", outcome.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
