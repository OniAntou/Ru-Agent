using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRunIdempotencyTests
{
    [Fact]
    public async Task Create_returns_201_and_idempotent_replay_returns_200_same_run()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var body = new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "hello tracer " + Guid.NewGuid().ToString("N"),
            preset = "standard",
            idempotency_key = "key-" + Guid.NewGuid().ToString("N"),
        };

        using var resp1 = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        var json1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var runId1 = json1.GetProperty("run_id").GetString()!;

        using var resp2 = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var json2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        var runId2 = json2.GetProperty("run_id").GetString()!;
        Assert.Equal(runId1, runId2);
    }

    [Fact]
    public async Task Same_key_different_normalized_request_returns_idempotency_conflict()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var key = "conflict-key-" + Guid.NewGuid().ToString("N");

        var body1 = new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "prompt one",
            preset = "standard",
            idempotency_key = key,
        };

        using var r1 = await client.PostAsJsonAsync("/agent/v1/runs", body1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var body2 = new
        {
            session_id = sessionId,
            workspace_path = tmp.Path,
            prompt = "prompt two different",
            preset = "standard",
            idempotency_key = key,
        };

        using var r2 = await client.PostAsJsonAsync("/agent/v1/runs", body2);
        Assert.Equal((HttpStatusCode)409, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_conflict", j.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Active_workspace_conflict_returns_workspace_busy()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session1 = await CreateSessionAsync(client);
        var session2 = await CreateSessionAsync(client);

        var body1 = new
        {
            session_id = session1,
            workspace_path = tmp.Path,
            prompt = "first run",
            preset = "standard",
            idempotency_key = "wk-busy-1-" + Guid.NewGuid().ToString("N"),
        };

        using var r1 = await client.PostAsJsonAsync("/agent/v1/runs", body1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var body2 = new
        {
            session_id = session2,
            workspace_path = tmp.Path,
            prompt = "second run same workspace",
            preset = "standard",
            idempotency_key = "wk-busy-2-" + Guid.NewGuid().ToString("N"),
        };

        using var r2 = await client.PostAsJsonAsync("/agent/v1/runs", body2);
        Assert.Equal((HttpStatusCode)409, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace_busy", j.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Preset_version_mismatch_returns_409()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        var body1 = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset test first",
            preset = "standard",
            idempotency_key = "preset-1-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 1,
        };

        using var r1 = await client.PostAsJsonAsync("/agent/v1/runs", body1);
        // First may be 201 or 409 if workspace busy from previous? Use fresh tmp so should be 201
        // Cancel previous run if needed to allow second
        if (r1.StatusCode == HttpStatusCode.Created)
        {
            var runId = (await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
            await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);
        }
        else
        {
            Assert.True(r1.StatusCode == HttpStatusCode.Created || (int)r1.StatusCode == 409);
        }

        // Second attempt with mismatched version
        var body2 = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "preset test second",
            preset = "standard",
            idempotency_key = "preset-2-" + Guid.NewGuid().ToString("N"),
            preset_selection_version = 2,
        };

        using var r2 = await client.PostAsJsonAsync("/agent/v1/runs", body2);
        Assert.Equal((HttpStatusCode)409, r2.StatusCode);
        var j = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_mismatch", j.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Full_preset_without_version_returns_preset_mismatch()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        var body = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "full without version",
            preset = "full_access",
            idempotency_key = "full-no-ver-" + Guid.NewGuid().ToString("N"),
        };

        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal((HttpStatusCode)409, resp.StatusCode);
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("preset_mismatch", j.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task First_standard_without_version_creates_version_1_atomically()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);

        var body = new
        {
            session_id = session,
            workspace_path = tmp.Path,
            prompt = "first standard no version",
            preset = "standard",
            idempotency_key = "first-std-no-ver-" + Guid.NewGuid().ToString("N"),
        };

        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("standard", json.GetProperty("preset").GetString());
        Assert.Equal(1, json.GetProperty("policy_snapshot_version").GetInt64());
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; }
        public RuBackend.Business.DataPaths DataPaths { get; }
        private readonly string _root;

        private TempWorkspace(string root, string wsPath, RuBackend.Business.DataPaths dataPaths)
        {
            _root = root;
            Path = wsPath;
            DataPaths = dataPaths;
        }

        public static TempWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-agent-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var dataPaths = new RuBackend.Business.DataPaths(
                System.IO.Path.Combine(dataRoot, "memory.db"),
                System.IO.Path.Combine(dataRoot, "settings.json"),
                System.IO.Path.Combine(dataRoot, "sessions"),
                System.IO.Path.Combine(dataRoot, "activity.jsonl")
            );
            // Actually DataPaths ctor used for tests is 6 args, but AgentHarnessDatabase derived from BusinessDataRoot
            // We need to create via DataPaths.Resolve
            var resolved = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new TempWorkspace(root, ws, resolved);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        }
    }
}
