using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using RuBackend.Harness.Workspace;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed partial class BusinessEndpointTests
{
    [Fact]
    public async Task Session_listing_does_not_label_an_unmarked_scratch_root_child_as_scratch()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();
        var sessions = factory.Services.GetRequiredService<ISessionStore>();
        var sessionId = (await sessions.CreateAsync(CancellationToken.None))!;
        var scratch = factory.Services.GetRequiredService<ScratchWorkspaceStore>();
        const string otherSessionId = "12121212121212121212121212121212";
        var path = scratch.PathFor(otherSessionId);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "user.txt"), "not Ru-owned");

        var creation = factory.Services.GetRequiredService<AgentRunCreationService>();
        var created = await creation.CreateRunAsync(new CreateAgentRunRequest(
            sessionId,
            path,
            "session list scratch provenance",
            "standard",
            null,
            "session-list-scratch-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal(201, created.StatusCode);

        var scratchSessionId = (await sessions.CreateAsync(CancellationToken.None))!;
        var scratchRun = await creation.CreateRunAsync(new CreateAgentRunRequest(
            scratchSessionId,
            string.Empty,
            "owned scratch session list provenance",
            "standard",
            null,
            "session-list-owned-scratch-" + Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Equal(201, scratchRun.StatusCode);

        var page = await client.GetFromJsonAsync<JsonElement>("/sessions/page?limit=100");
        var rows = page.GetProperty("sessions").EnumerateArray().ToArray();
        var row = rows
            .Single(item => item.GetProperty("id").GetString() == sessionId);
        var scratchRow = rows
            .Single(item => item.GetProperty("id").GetString() == scratchSessionId);
        Assert.Equal("project", row.GetProperty("workspace_kind").GetString());
        Assert.Equal("scratch", scratchRow.GetProperty("workspace_kind").GetString());
    }

    [Theory]
    [InlineData("/sessions/page?limit=0")]
    [InlineData("/sessions/page?limit=1&limit=2")]
    [InlineData("/sessions/page?unexpected=x")]
    [InlineData("/sessions/search?query=%20")]
    [InlineData("/sessions/missing/messages?query=x")]
    public async Task History_routes_reject_invalid_arguments(string route)
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task History_pages_preserve_absolute_indices_across_appends_and_invalidate_on_rewind()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var id = (await store.CreateAsync(CancellationToken.None))!;
        for (var start = 0; start < 450; start += 50)
            Assert.True(await store.AppendAsync(id, Enumerable.Range(start, 50)
                .Select(i => new SessionMessage(i % 2 == 0 ? "user" : "ai", $"retained history {i}")).ToArray(), CancellationToken.None));
        var first = await client.GetFromJsonAsync<JsonElement>($"/sessions/{id}/messages?limit=200");
        Assert.Equal(250, first.GetProperty("start_index").GetInt32());
        Assert.Equal(450, first.GetProperty("total_messages").GetInt32());
        var cursor = Uri.EscapeDataString(first.GetProperty("next_cursor").GetString()!);
        Assert.True(await store.AppendAsync(id, [new("user", "new append")], CancellationToken.None));
        var second = await client.GetFromJsonAsync<JsonElement>($"/sessions/{id}/messages?cursor={cursor}");
        Assert.Equal(50, second.GetProperty("start_index").GetInt32());
        Assert.Equal(450, second.GetProperty("total_messages").GetInt32());
        Assert.Equal("retained history 50", second.GetProperty("messages")[0].GetProperty("content").GetString());
        var other = (await store.CreateAsync(CancellationToken.None))!;
        using var wrongSession = await client.GetAsync($"/sessions/{other}/messages?cursor={cursor}");
        Assert.Equal(HttpStatusCode.BadRequest, wrongSession.StatusCode);
        Assert.NotNull(await store.RewindAsync(id, 400, CancellationToken.None));
        using var stale = await client.GetAsync($"/sessions/{id}/messages?cursor={cursor}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("session_history_cursor_stale", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task History_search_and_summary_paging_reach_older_sessions()
    {
        using var data = TemporaryBusinessData.Create();
        using var factory = CreateAspNetFactory(data.Paths);
        using var client = factory.CreateAuthenticatedClient();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        for (var i = 0; i < 5; i++)
        {
            var id = (await store.CreateAsync(CancellationToken.None))!;
            Assert.True(await store.AppendAsync(id, [new("user", $"needle unique {i}")], CancellationToken.None));
        }
        var first = await client.GetFromJsonAsync<JsonElement>("/sessions/page?limit=2&query=needle");
        var cursor = Uri.EscapeDataString(first.GetProperty("next_cursor").GetString()!);
        var next = await client.GetFromJsonAsync<JsonElement>($"/sessions/page?limit=2&query=needle&cursor={cursor}");
        Assert.Equal(2, next.GetProperty("sessions").GetArrayLength());
        Assert.Empty(first.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetString())
            .Intersect(next.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetString())));
        var hits = await client.GetFromJsonAsync<JsonElement>("/sessions/search?query=needle&limit=3");
        Assert.Equal(3, hits.GetProperty("hits").GetArrayLength());
        Assert.All(hits.GetProperty("hits").EnumerateArray(), hit => Assert.Equal(0, hit.GetProperty("message_index").GetInt32()));
    }
}
