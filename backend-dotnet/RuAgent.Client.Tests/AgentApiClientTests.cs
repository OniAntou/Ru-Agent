using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentApiClientTests
{
    private static PublicHostSession Session(string bearer = "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, "http://127.0.0.1:8000/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    [Fact]
    public async Task GetRunStrictParsingRejectsUnknownFields()
    {
        var s = Session();
        var handler = new FakeHandler(req =>
        {
            Assert.Contains("/agent/v1/runs/run_", req.RequestUri!.ToString());
            var payload = new { run_id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", session_id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", workspace_id = "ws_123", status = "running", preset = "standard", policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64), version = 0, last_sequence = 1, created_at = DateTimeOffset.UtcNow.ToString("O"), updated_at = DateTimeOffset.UtcNow.ToString("O"), unknown = "field" };
            var json = JsonSerializer.Serialize(payload);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new AgentApiClient(s, new HttpClient(handler));
        await Assert.ThrowsAsync<AgentApiException>(() => client.GetRunAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public async Task GetPayloadRunBindingEnforced()
    {
        var s = Session();
        var handler = new FakeHandler(_ =>
        {
            var json = JsonSerializer.Serialize(new { run_id = "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", payload_id = "payload_cccccccccccccccccccccccccccccccc", kind = "user_prompt", encoding = "utf8_text", content = "hello" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new AgentApiClient(s, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.GetPayloadAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "payload_cccccccccccccccccccccccccccccccc"));
        Assert.Equal("payload_run_mismatch", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateRunUsesBearerAndStrictDto()
    {
        var bearer = new string('d', 64);
        var s = Session(bearer);
        var handler = new FakeHandler(req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(bearer, req.Headers.Authorization?.Parameter);
            var resp = new CreateAgentRunResponse("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "ws_1", "waiting_history", "standard", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(JsonSerializer.Serialize(resp), Encoding.UTF8, "application/json") };
        });
        var client = new AgentApiClient(s, new HttpClient(handler));
        var req = new CreateAgentRunRequest("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "D:\\LLM", "hello", "standard", 1, "idem-key-123");
        var resp = await client.CreateRunAsync(req);
        Assert.Equal("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", resp.RunId);
    }

    [Fact]
    public async Task CancelRejectsWrongBearer()
    {
        var s = Session();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error_code\":\"unauthorized\"}") });
        var client = new AgentApiClient(s, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AgentApiException>(() => client.CancelAsync("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal("unauthorized", ex.ErrorCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_func(request));
    }
}
