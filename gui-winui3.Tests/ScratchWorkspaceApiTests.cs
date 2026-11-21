using System.Net;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ScratchWorkspaceApiTests
{
    [Fact]
    public async Task Scratch_workspace_uses_the_host_route_and_returns_the_canonical_path()
    {
        using var handler = new Handler(
            HttpStatusCode.OK,
            """{"status":"ok","workspace_path":"C:\\data\\Ru\\workspaces\\synthetic","workspace_kind":"scratch"}""");
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic"), handler);

        var result = await api.EnsureScratchWorkspaceAsync("synthetic-session");

        Assert.NotNull(result);
        Assert.Equal(@"C:\data\Ru\workspaces\synthetic", result!.Path);
        Assert.Equal("scratch", result.Kind);
        Assert.Equal("/sessions/synthetic-session/workspace/scratch", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("gui", handler.ClientSource);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, """{"status":"error","code":"session_not_found"}""")]
    [InlineData(HttpStatusCode.OK, """{"status":"ok","workspace_path":"","workspace_kind":"scratch"}""")]
    [InlineData(HttpStatusCode.OK, """{"status":"ok","workspace_path":"C:\\workspace","workspace_kind":"project"}""")]
    public async Task A_refused_or_malformed_scratch_reply_is_not_used(HttpStatusCode status, string body)
    {
        using var handler = new Handler(status, body);
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic"), handler);

        Assert.Null(await api.EnsureScratchWorkspaceAsync("synthetic-session"));
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string? ClientSource { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Method = request.Method;
            ClientSource = request.Headers.TryGetValues("X-Ru-Client-Source", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }
}
