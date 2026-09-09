using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentUsageEnrichmentTests
{
    [Fact]
    public async Task Sse_Envelopes_Carry_Usage_After_ModelUsageRecorded()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "usage seed", "usage-seed-" + Guid.NewGuid().ToString("N"));
        var runIdObj = new RunId(runId);
        var store = factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = await store.GetRunAsync(runIdObj, CancellationToken.None)
            ?? throw new InvalidOperationException("run not found");

        var usagePayloadId = new PayloadId("payload_" + new string('d', 32));
        var usageBytes = Encoding.UTF8.GetBytes("""{"input_tokens":1000,"output_tokens":200,"total_tokens":1200}""");
        var usagePayload = new HarnessPayloadIntent(
            usagePayloadId,
            HarnessPayloadKind.ModelUsage,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            16 * 1024,
            usageBytes);
        var stepId = new ModelStepId("step_" + new string('d', 32));
        var completedEvent = new HarnessEventIntent(
            new EventId("evt_" + new string('e', 32)),
            HarnessEventKind.ModelStepCompleted,
            modelStepId: stepId,
            safeCount: 0,
            safeCode: "succeeded");
        var usageEvent = new HarnessEventIntent(
            new EventId("evt_" + new string('f', 32)),
            HarnessEventKind.ModelUsageRecorded,
            modelStepId: stepId,
            payloadId: usagePayloadId,
            safeCode: "reported");
        var nextRun = CopyRun(run, run.Version + 1, run.LastSequence + 2);
        await store.CommitAsync(
            new HarnessCommit(
                run.RunId,
                run.Version,
                nextRun,
                [completedEvent, usageEvent],
                [usagePayload],
                []),
            CancellationToken.None);

        var events = await ReadEventsAsync(client, runId);
        var dump = string.Join(" | ", events.Select(e => $"{TypeOf(e)}#{SeqOf(e)}[total={TotalOf(e)}]"));
        var usageIdx = events.FindIndex(e => TypeOf(e) == "model.usage_recorded");
        Assert.True(usageIdx >= 0, $"no model.usage_recorded. feed={dump}");
        var usageEnvelope = events[usageIdx];
        Assert.True(usageEnvelope.TryGetProperty("total_tokens", out var totalProp), $"missing total_tokens on usage envelope. feed={dump}");
        Assert.Equal(1200, totalProp.GetInt64());
        Assert.Equal(1000, usageEnvelope.GetProperty("input_tokens").GetInt64());
        Assert.Equal(200, usageEnvelope.GetProperty("output_tokens").GetInt64());
    }

    [Fact]
    public async Task Sse_Envelopes_Omit_Usage_Keys_When_Run_Has_No_Usage()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "plain seed", "plain-seed-" + Guid.NewGuid().ToString("N"));
        var runIdObj = new RunId(runId);
        var store = factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = await store.GetRunAsync(runIdObj, CancellationToken.None)
            ?? throw new InvalidOperationException("run not found");

        var stepId = new ModelStepId("step_" + new string('d', 32));
        var completedEvent = new HarnessEventIntent(
            new EventId("evt_" + new string('e', 32)),
            HarnessEventKind.ModelStepCompleted,
            modelStepId: stepId,
            safeCount: 0,
            safeCode: "succeeded");
        var nextRun = CopyRun(run, run.Version + 1, run.LastSequence + 1);
        await store.CommitAsync(
            new HarnessCommit(
                run.RunId,
                run.Version,
                nextRun,
                [completedEvent],
                [],
                []),
            CancellationToken.None);

        var events = await ReadEventsAsync(client, runId);
        Assert.NotEmpty(events);
        foreach (var e in events)
        {
            Assert.False(e.TryGetProperty("total_tokens", out _));
            Assert.False(e.TryGetProperty("input_tokens", out _));
            Assert.False(e.TryGetProperty("output_tokens", out _));
        }
    }

    private static string TypeOf(JsonElement e) =>
        e.TryGetProperty("event_type", out var t) ? t.GetString() ?? "?" : "?";

    private static long SeqOf(JsonElement e) =>
        e.TryGetProperty("sequence", out var s) ? s.GetInt64() : -1;

    private static string TotalOf(JsonElement e) =>
        e.TryGetProperty("total_tokens", out var t) ? t.ToString() : "-";

    private static AgentRun CopyRun(AgentRun current, long version, long lastSequence) =>
        new(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            current.Status,
            version,
            lastSequence,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            current.UpdatedAt.AddTicks(1),
            current.TerminalAt,
            current.TerminalErrorCode);

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static async Task<string> CreateRunAsync(HttpClient client, string sessionId, string workspacePath, string prompt, string key)
    {
        Directory.CreateDirectory(workspacePath);
        var body = new { session_id = sessionId, workspace_path = workspacePath, prompt, preset = "standard", idempotency_key = key };
        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        if (resp.StatusCode != HttpStatusCode.Created && resp.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"create failed {resp.StatusCode} {json}");
        }

        return json.GetProperty("run_id").GetString()!;
    }

    private static async Task<List<JsonElement>> ReadEventsAsync(HttpClient client, string runId)
    {
        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}/events?live=0");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var events = new List<JsonElement>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data: ".Length..];
            if (payload.Contains("\"event_type\"", StringComparison.Ordinal))
            {
                events.Add(JsonDocument.Parse(payload).RootElement.Clone());
            }
        }

        return events;
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
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-usage-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var resolved = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new TempWorkspace(root, ws, resolved);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        }
    }
}
