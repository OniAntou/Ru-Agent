using System.Net;
using System.Text;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SessionPresetReadTests
{
    [Theory]
    [InlineData("plan")] [InlineData("standard")] [InlineData("full_access")]
    public async Task Reads_a_selection_without_posting_or_sharing_it_with_another_session(string preset)
    {
        using var handler = new Handler($$"""{"session_id":"session_a","preset":"{{preset}}","version":3}""");
        using var http = new HttpClient(handler);
        using var client = new AgentApiClient(new PublicHostSession(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!), http);
        var read = await client.GetSessionPresetAsync("session_a");
        Assert.Equal(preset, read!.Value.Preset); Assert.Equal(3, read.Value.Version);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.EndsWith("/session_a/preset", handler.Path);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.GetSessionPresetAsync("session_b"));
        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Theory]
    [InlineData("{\"session_id\":\"session_a\",\"preset\":\"unknown\",\"version\":1}")]
    [InlineData("{\"session_id\":\"session_a\",\"preset\":\"standard\",\"version\":-1}")]
    [InlineData("{\"session_id\":\"session_a\",\"preset\":\"standard\",\"version\":\"1\"}")]
    public async Task Invalid_selections_fail_closed(string body)
    {
        using var handler = new Handler(body); using var http = new HttpClient(handler);
        using var client = new AgentApiClient(new PublicHostSession(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!), http);
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<AgentApiException>(() => client.GetSessionPresetAsync("session_a"))).ErrorCode);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        internal HttpMethod? Method;
        internal string Path = string.Empty;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method; Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
