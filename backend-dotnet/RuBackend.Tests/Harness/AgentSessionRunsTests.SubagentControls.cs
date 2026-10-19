using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentSessionRunsTests
{
    [Fact]
    public async Task Subagent_HTTP_is_authenticated_owned_bounded_and_read_only_for_every_root_state()
    {
        using var tmp = TempWorkspace.Create();
        using var host = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = host.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);
        var rootId = await CreateRunAsync(client, session, tmp.Path, "root", Guid.NewGuid().ToString("N"));
        var events = host.Services.GetRequiredService<IHarnessEventStore>();
        var root = (await events.GetRunAsync(new(rootId), default))!;
        var child = new AgentRun(new("run_" + Guid.NewGuid().ToString("N")), new(Guid.NewGuid().ToString("N")), root.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Preset, root.PolicySnapshotVersion, root.PolicySnapshotSha256,
            root.CreatedAt, root.CreatedAt);
        var agents = host.Services.GetRequiredService<SqliteSubagentStore>();
        await agents.CreateAsync(new("agent_one", root.RunId, "fixture_spawn", SubagentRole.Review,
            new(child, [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunStateChanged, safeCode: "subagent_queued")], [], [],
                new(new string('c', 64), new string('d', 64), 1))), default);
        var path = $"/agent/v1/runs/{rootId}/subagents";
        using (var anonymous = host.CreateClient())
        {
            foreach (var (method, suffix) in new[] { (HttpMethod.Get, ""), (HttpMethod.Get, "/agent_one"),
                (HttpMethod.Post, "/agent_one/messages"), (HttpMethod.Post, "/agent_one/cancel"), (HttpMethod.Post, "/agent_one/apply") })
            {
                using var request = new HttpRequestMessage(method, path + suffix);
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
            }
        }
        var list = await client.GetFromJsonAsync<SubagentListResponse>(path);
        Assert.Equal("agent_one", Assert.Single(list!.Agents).AgentId);
        Assert.Null(Assert.Single(list.Agents[0].Attempts).Model);
        Assert.False(list.Usage.IsComplete);
        Assert.Equal(list.SnapshotId, (await client.GetFromJsonAsync<SubagentListResponse>(path))!.SnapshotId);
        foreach (var action in new[] { "messages", "cancel", "apply" })
        {
            using var response = await client.PostAsJsonAsync(path + "/agent_one/" + action,
                new { message_id = "user_fixture", message = "Check cancellation", result_version = 1 });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/agent_one?result_version=1&offset=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/agent_other")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/agent/v1/runs/{child.RunId.Value}/subagents")).StatusCode);
        await client.PostAsync($"/agent/v1/runs/{rootId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        foreach (var action in new[] { "messages", "cancel", "apply" })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsJsonAsync(path + "/agent_one/" + action, new { })).StatusCode);
    }
}
