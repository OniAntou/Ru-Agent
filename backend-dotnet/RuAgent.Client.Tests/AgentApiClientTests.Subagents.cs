using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed partial class AgentApiClientTests
{
    [Fact]
    public async Task Idempotent_create_replay_accepts_parent_already_waiting_for_children()
    {
        const string session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var body = new CreateAgentRunResponse("run_" + session, session, "ws_1", "waiting_subagents", "standard",
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") })));
        Assert.Equal("waiting_subagents", (await client.CreateRunAsync(Request(session, "standard"))).Status);
    }
}
