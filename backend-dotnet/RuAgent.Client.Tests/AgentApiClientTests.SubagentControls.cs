using System.Net;
using System.Text;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed partial class AgentApiClientTests
{
    [Fact]
    public async Task Subagent_reads_bind_root_and_agent_and_keep_bearer_on_request()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new FakeHandler(r =>
        {
            calls.Add(r.Method + " " + r.RequestUri!.PathAndQuery);
            Assert.NotNull(r.Headers.Authorization);
            var body = r.Method == HttpMethod.Get
                ? "{\"root_run_id\":\"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"root_status\":\"waiting_subagents\",\"snapshot_id\":\"snapshot\",\"agents\":[],\"preparations\":[],\"unavailable_attempts\":0,\"usage\":{\"model_steps\":0,\"reported_steps\":0,\"is_complete\":false}}"
                : "{\"agent_id\":\"agent_one\",\"accepted\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        using var client = new AgentApiClient(Session(), http);
        const string root = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.Equal("waiting_subagents", (await client.ListSubagentsAsync(root)).RootStatus);
        Assert.Equal(new[] { $"GET /agent/v1/runs/{root}/subagents" }, calls);
        await Assert.ThrowsAsync<AgentApiException>(() => client.GetSubagentAsync(root, "../another"));
        var methods = typeof(AgentApiClient).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain("SendSubagentAsync", methods);
        Assert.DoesNotContain("CancelSubagentAsync", methods);
        Assert.DoesNotContain("ApplySubagentAsync", methods);
    }
}
