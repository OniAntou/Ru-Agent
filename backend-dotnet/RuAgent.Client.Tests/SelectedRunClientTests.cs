using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SelectedRunClientTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Explicit_selection_uses_the_supported_route_and_survives_acknowledgement_retry(bool loseFirstAck)
    {
        using var handler = new Handler(loseFirstAck); using var http = new HttpClient(handler);
        using var api = new AgentApiClient(new PublicHostSession(new Uri("http://127.0.0.1:1/"), "synthetic", null!), http);
        var request = new CreateAgentRunRequest("session_a", "C:/workspace", "task", "standard", 1, "same-key",
            ProviderSelection: new("OpenAI", "old-model"));
        await api.CreateRunAsync(request);
        Assert.Equal(loseFirstAck ? 2 : 1, handler.Bodies.Count);
        Assert.All(handler.Paths, path => Assert.Equal("/agent/v1/runs/selected", path));
        Assert.All(handler.Bodies, body => Assert.Equal(handler.Bodies[0], body));
        using var parsed = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("old-model", parsed.RootElement.GetProperty("provider_selection").GetProperty("model").GetString());
    }

    [Fact]
    public async Task Old_host_refusal_never_falls_back_to_an_unselected_run()
    {
        using var handler = new Handler(false) { Unsupported = true }; using var http = new HttpClient(handler);
        using var api = new AgentApiClient(new PublicHostSession(new Uri("http://127.0.0.1:1/"), "synthetic", null!), http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => api.CreateRunAsync(new("session_a", "C:/workspace", "task", "standard", 1, "key",
            ProviderSelection: new("OpenAI", "old-model"))));
        Assert.Equal("provider_selection_unsupported", error.ErrorCode);
        Assert.False(error.IsCreateOutcomeUnknown);
        Assert.Single(handler.Paths);
        Assert.Equal("/agent/v1/runs/selected", handler.Paths[0]);
    }

    private sealed class Handler(bool loseFirstAck) : HttpMessageHandler
    {
        internal bool Unsupported;
        internal readonly List<string> Paths = [], Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath); Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if (loseFirstAck && Paths.Count == 1) throw new HttpRequestException("synthetic lost acknowledgement");
            return new HttpResponseMessage(Unsupported ? HttpStatusCode.NotFound : HttpStatusCode.Created)
            { Content = new StringContent(Unsupported ? "{}" : JsonSerializer.Serialize(new CreateAgentRunResponse(
                "run_" + new string('a', 32), "session_a", "ws_" + new string('a', 32), "running", "standard", "2026-10-30T00:00:00Z", "2026-10-30T00:00:00Z")), Encoding.UTF8, "application/json") };
        }
    }
}
