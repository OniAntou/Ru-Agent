using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Persistence;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentSessionRunsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Internal_session_marker_blocks_public_creation_and_session_history(bool createRun)
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var rootSession = await CreateSessionAsync(client);
        var root = await CreateRunAsync(client, rootSession, tmp.Path, "root", Guid.NewGuid().ToString("N"));
        await client.PostAsync($"/agent/v1/runs/{root}/cancel", null);
        var internalSession = await CreateSessionAsync(client);
        var child = await CreateRunAsync(client, internalSession, tmp.Path, "child", Guid.NewGuid().ToString("N"));
        await client.PostAsync($"/agent/v1/runs/{child}/cancel", null);

        // The durable marker must survive lost subagent relation metadata. Even a
        // session document with the same ID cannot turn it back into a root session.
        var options = factory.Services.GetRequiredService<HarnessSqliteOptions>();
        await using (var connection = await options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO agent_internal_runs(run_id,root_run_id) VALUES($child,$root)";
            command.Parameters.AddWithValue("$child", child);
            command.Parameters.AddWithValue("$root", root);
            await command.ExecuteNonQueryAsync();
        }

        using var response = createRun
            ? await client.PostAsJsonAsync("/agent/v1/runs", new
            {
                session_id = internalSession, workspace_path = tmp.Path, prompt = "new root",
                idempotency_key = Guid.NewGuid().ToString("N"),
            })
            : await client.GetAsync($"/agent/v1/sessions/{internalSession}/runs?limit=1");
        Assert.Equal(createRun ? HttpStatusCode.Conflict : HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(createRun ? "subagent_session_internal" : "session_not_found", json.GetProperty("error_code").GetString());

        using var visible = await client.GetAsync($"/agent/v1/sessions/{rootSession}/runs?limit=1");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        var history = await visible.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(root, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("run_id").GetString());
    }
}
