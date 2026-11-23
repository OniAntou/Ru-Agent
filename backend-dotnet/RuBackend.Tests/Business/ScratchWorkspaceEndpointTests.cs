using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class ScratchWorkspaceEndpointTests : IDisposable
{
    private readonly string _root;
    private readonly DataPaths _paths;
    private readonly BackendWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ScratchWorkspaceEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-scratch-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new DataPaths(
            Path.Combine(_root, "settings.json"),
            Path.Combine(_root, "sessions"),
            Path.Combine(_root, "activity.jsonl"));
        _factory = new BackendWebApplicationFactory(dataPaths: _paths);
        _client = _factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task Folderless_session_gets_a_host_owned_scratch_workspace_before_its_first_run()
    {
        string sessionId = await CreateSessionAsync();

        using var response = await _client.PostAsync(
            $"/sessions/{sessionId}/workspace/scratch", content: null);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", payload.GetProperty("status").GetString());
        Assert.Equal("scratch", payload.GetProperty("workspace_kind").GetString());
        string path = payload.GetProperty("workspace_path").GetString()!;
        Assert.Equal(
            Path.Combine(_paths.BusinessDataRoot, "workspaces", sessionId),
            path);
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, ".ru-scratch.json")));

        // Re-opening a draft before its first send asks again; the Host must not allocate
        // a second workspace or relabel the existing one.
        using var second = await _client.PostAsync(
            $"/sessions/{sessionId}/workspace/scratch", content: null);
        var secondPayload = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(path, secondPayload.GetProperty("workspace_path").GetString());
    }

    [Fact]
    public async Task Scratch_workspace_is_deleted_when_an_empty_draft_is_cleaned_up()
    {
        string sessionId = await CreateSessionAsync();
        using var ensure = await _client.PostAsync(
            $"/sessions/{sessionId}/workspace/scratch", content: null);
        string path = (await ensure.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("workspace_path").GetString()!;
        Assert.True(Directory.Exists(path));

        using var cleanup = await _client.DeleteAsync($"/sessions/{sessionId}?if_empty=true");
        var cleanupPayload = await cleanup.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", cleanupPayload.GetProperty("status").GetString());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task Scratch_workspace_is_kept_when_if_empty_cleanup_preserves_a_started_chat()
    {
        string sessionId = await CreateSessionAsync();
        using var ensure = await _client.PostAsync(
            $"/sessions/{sessionId}/workspace/scratch", content: null);
        string path = (await ensure.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("workspace_path").GetString()!;
        var sessions = _factory.Services.GetRequiredService<ISessionStore>();
        Assert.True(await sessions.AppendAsync(
            sessionId,
            [new SessionMessage("user", "first prompt")],
            CancellationToken.None));

        // A late draft-cleanup races the first accepted prompt: the store preserves the
        // started chat, and its scratch workspace must survive with it. Deleting the folder
        // here would destroy the files the accepted run and its staged attachments use.
        using var cleanup = await _client.DeleteAsync($"/sessions/{sessionId}?if_empty=true");
        var cleanupPayload = await cleanup.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", cleanupPayload.GetProperty("status").GetString());
        Assert.NotNull(await sessions.GetAsync(sessionId, CancellationToken.None));
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, ".ru-scratch.json")));
    }

    [Fact]
    public async Task The_scratch_route_repairs_a_marker_the_agent_damaged()
    {
        string sessionId = await CreateSessionAsync();
        using var first = await _client.PostAsync($"/sessions/{sessionId}/workspace/scratch", content: null);
        string path = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("workspace_path").GetString()!;
        var marker = Path.Combine(path, ".ru-scratch.json");
        await File.WriteAllTextAsync(marker, "{\"note\":\"rewritten inside the workspace\"}");

        using var second = await _client.PostAsync($"/sessions/{sessionId}/workspace/scratch", content: null);
        var payload = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(path, payload.GetProperty("workspace_path").GetString());
        Assert.Contains(sessionId, await File.ReadAllTextAsync(marker), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scratch_route_refuses_a_marker_it_cannot_repair_and_says_what_to_do()
    {
        string sessionId = await CreateSessionAsync();
        // A folder Ru never allocated: no record, and a marker it does not own.
        var path = Path.Combine(_paths.BusinessDataRoot, "workspaces", sessionId);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, ".ru-scratch.json"), "{\"note\":\"placed by hand\"}");

        using var response = await _client.PostAsync($"/sessions/{sessionId}/workspace/scratch", content: null);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("scratch_workspace_unavailable", payload.GetProperty("code").GetString());
        Assert.Contains(
            "Rename or delete that folder",
            payload.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_scratch_route_refuses_an_unknown_or_malformed_session()
    {
        using var unknown = await _client.PostAsync(
            $"/sessions/{new string('a', 32)}/workspace/scratch", content: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(
            "session_not_found",
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var malformed = await _client.PostAsync(
            "/sessions/not-a-session/workspace/scratch", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(
            "invalid_session_id",
            (await malformed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private async Task<string> CreateSessionAsync()
    {
        using var response = await _client.PostAsync("/sessions/new", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session_id").GetString()!;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteTestPools.ClearHarnessDatabase(_paths.AgentHarnessDatabase);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
