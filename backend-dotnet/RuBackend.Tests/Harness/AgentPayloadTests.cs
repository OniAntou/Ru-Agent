using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RuBackend.Business.Settings;
using RuBackend.Tests;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentPayloadTests
{
    [Fact]
    public async Task GetPayload_returns_image_dataUrl()
    {
        using var tmp = TempWorkspace.Create();
        var snapshot = CreateSettingsSnapshot("OpenAI", "gpt-4o");
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths, settingsStore: new StubSettingsStore(snapshot));
        using var client = factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var validPngBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        using var response = await client.PostAsJsonAsync(
            "/agent/v1/runs",
            new
            {
                session_id = sessionId,
                workspace_path = tmp.Path,
                prompt = "analyze this image",
                preset = "standard",
                idempotency_key = "payload-test-" + Guid.NewGuid().ToString("N"),
                image_attachments = new[] { validPngBase64 },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = json.GetProperty("run_id").GetString()!;

        // Query payload id from database
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tmp.DataPaths.AgentHarnessDatabase}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_id FROM agent_payloads WHERE run_id = $runId AND kind = 11";
        cmd.Parameters.AddWithValue("$runId", runId);
        var payloadId = (string)(await cmd.ExecuteScalarAsync())!;

        using var getPayload = await client.GetAsync($"/agent/v1/runs/{runId}/payloads/{payloadId}");
        Assert.Equal(HttpStatusCode.OK, getPayload.StatusCode);
        var payloadJson = await getPayload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("userimage", payloadJson.GetProperty("kind").GetString());
        Assert.Equal(validPngBase64, payloadJson.GetProperty("content").GetString());
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static SettingsSnapshot CreateSettingsSnapshot(string backend, string model) => new(
        new System.Text.Json.Nodes.JsonObject(),
        backend,
        model,
        OpenCodeModel: backend == "OpenCode Zen" ? model : string.Empty,
        KiloModel: backend == "Kilo Gateway" ? model : string.Empty,
        ApiVariant: "Default",
        KiloReasoningEnabled: false,
        CustomModel: string.Empty,
        CustomUrl: "http://localhost:8000/v1",
        OpenAiModel: backend == "OpenAI" ? model : string.Empty,
        AnthropicModel: backend == "Anthropic" ? model : string.Empty);

    private sealed class StubSettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) => Task.CompletedTask;
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
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-payload-test-" + Guid.NewGuid().ToString("N"));
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
