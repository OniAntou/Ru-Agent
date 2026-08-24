using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentSessionRunsTests
{
    [Fact]
    public async Task Session_runs_return_in_deterministic_order_and_pagination_and_no_leakage()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var sessionA = await CreateSessionAsync(client);
        var sessionB = await CreateSessionAsync(client);

        var runIdsA = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var runId = await CreateRunAsync(client, sessionA, tmp.Path, $"prompt A{i}", "std-key-A" + i + Guid.NewGuid().ToString("N"));
            runIdsA.Add(runId);
            // Cancel to allow next run in same session (active session constraint)
            await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);
            await Task.Delay(10);
        }

        // Create one run in session B
        var runB = await CreateRunAsync(client, sessionB, tmp.Path + "-b-" + Guid.NewGuid().ToString("N"), "prompt B", "key-B-" + Guid.NewGuid().ToString("N"));

        // Get session A runs
        using var respA = await client.GetAsync($"/agent/v1/sessions/{sessionA}/runs?limit=10");
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var jsonA = await respA.Content.ReadFromJsonAsync<JsonElement>();
        var itemsA = jsonA.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, itemsA.Length);
        // Verify descending order created_at, run_id
        for (int i = 0; i < itemsA.Length - 1; i++)
        {
            var t1 = DateTimeOffset.Parse(itemsA[i].GetProperty("created_at").GetString()!);
            var t2 = DateTimeOffset.Parse(itemsA[i + 1].GetProperty("created_at").GetString()!);
            Assert.True(t1 >= t2);
            if (t1 == t2)
            {
                Assert.True(string.Compare(itemsA[i].GetProperty("run_id").GetString(), itemsA[i + 1].GetProperty("run_id").GetString(), StringComparison.Ordinal) > 0);
            }
        }

        // No leakage: ensure none of session B's run appears in A
        Assert.All(itemsA, item => Assert.NotEqual(runB, item.GetProperty("run_id").GetString()));

        // Get session B runs
        using var respB = await client.GetAsync($"/agent/v1/sessions/{sessionB}/runs");
        var jsonB = await respB.Content.ReadFromJsonAsync<JsonElement>();
        var itemsB = jsonB.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(itemsB);
        Assert.Equal(runB, itemsB[0].GetProperty("run_id").GetString());
    }

    [Fact]
    public async Task Pagination_limit_and_cursor_behavior()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var created = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var ws = tmp.Path + "-p" + i + "-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(ws);
            var runId = await CreateRunAsync(client, session, ws, $"prompt {i}", "pag-key-" + i + Guid.NewGuid().ToString("N"));
            created.Add(runId);
            await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);
            await Task.Delay(5);
        }

        // limit default 50
        using var respDefault = await client.GetAsync($"/agent/v1/sessions/{session}/runs");
        Assert.Equal(HttpStatusCode.OK, respDefault.StatusCode);
        var jDef = await respDefault.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, jDef.GetProperty("items").GetArrayLength());
        Assert.True(jDef.GetProperty("next_cursor").ValueKind == JsonValueKind.Null);

        // limit 2 with pagination
        using var resp1 = await client.GetAsync($"/agent/v1/sessions/{session}/runs?limit=2");
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var j1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, j1.GetProperty("items").GetArrayLength());
        var cur = j1.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cur));

        using var resp2 = await client.GetAsync($"/agent/v1/sessions/{session}/runs?limit=2&cursor={Uri.EscapeDataString(cur!)}");
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var j2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, j2.GetProperty("items").GetArrayLength());

        // limit out of range
        using var respBadLimit = await client.GetAsync($"/agent/v1/sessions/{session}/runs?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, respBadLimit.StatusCode);

        using var respBadLimit2 = await client.GetAsync($"/agent/v1/sessions/{session}/runs?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, respBadLimit2.StatusCode);
    }

    [Fact]
    public async Task Malformed_and_foreign_cursors_rejected_without_exposing_contents()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var sessionA = await CreateSessionAsync(client);
        var sessionB = await CreateSessionAsync(client);

        var wsA = tmp.Path + "-a-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(wsA);
        var runA = await CreateRunAsync(client, sessionA, wsA, "prompt", "key-a-" + Guid.NewGuid().ToString("N"));
        await client.PostAsync($"/agent/v1/runs/{runA}/cancel", null);

        // Get a valid cursor from session A
        using var resp = await client.GetAsync($"/agent/v1/sessions/{sessionA}/runs?limit=1");
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var validCursor = j.GetProperty("next_cursor").ValueKind == JsonValueKind.String ? j.GetProperty("next_cursor").GetString() : null;
        // If only one item, next_cursor may be null; create more runs to ensure cursor exists
        if (validCursor is null)
        {
            for (int i = 0; i < 2; i++)
            {
                var ws = tmp.Path + "-extra-" + i + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(ws);
                var r = await CreateRunAsync(client, sessionA, ws, $"p{i}", "k-" + i + Guid.NewGuid().ToString("N"));
                await client.PostAsync($"/agent/v1/runs/{r}/cancel", null);
            }

            using var resp2 = await client.GetAsync($"/agent/v1/sessions/{sessionA}/runs?limit=1");
            var j2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
            validCursor = j2.GetProperty("next_cursor").GetString();
        }

        Assert.False(string.IsNullOrWhiteSpace(validCursor));

        // Malformed cursor
        using var bad = await client.GetAsync($"/agent/v1/sessions/{sessionA}/runs?cursor=not-base64!!");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badBody = await bad.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not-base64", badBody);
        Assert.Contains("invalid_cursor", badBody);

        // Foreign cursor: use session A's cursor for session B
        using var foreign = await client.GetAsync($"/agent/v1/sessions/{sessionB}/runs?cursor={Uri.EscapeDataString(validCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        var foreignBody = await foreign.Content.ReadAsStringAsync();
        Assert.Contains("invalid_cursor", foreignBody);
        Assert.DoesNotContain(validCursor!, foreignBody);
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static async Task<string> CreateRunAsync(HttpClient client, string sessionId, string workspacePath, string prompt, string key)
    {
        Directory.CreateDirectory(workspacePath);
        var body = new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt,
            preset = "standard",
            idempotency_key = key,
        };

        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        if (resp.StatusCode != HttpStatusCode.Created)
        {
            var txt = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Create run failed {resp.StatusCode} {txt}");
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("run_id").GetString()!;
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
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-session-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
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
