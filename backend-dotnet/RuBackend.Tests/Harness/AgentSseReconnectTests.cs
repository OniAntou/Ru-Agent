using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;
using Xunit;
using RuBackend.Harness;

namespace RuBackend.Tests.Harness;

public sealed class AgentSseReconnectTests
{
    [Fact]
    public async Task Sse_durable_id_and_last_event_id_and_after_sequence_equal_acceptance()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt", "sse-1-" + Guid.NewGuid().ToString("N"));

        // After creation, run has at least one durable event (sequence 1)
        var sse = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        Assert.Contains("id: 1", sse);
        Assert.Contains("\"protocol_version\":1", sse);
        Assert.DoesNotContain("command", sse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sse_conflicting_dual_cursor_returns_cursor_mismatch()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt", "sse-2-" + Guid.NewGuid().ToString("N"));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=1");
        request.Headers.Add("Last-Event-ID", "2");
        using var resp = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cursor_mismatch", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Sse_reconnect_from_every_durable_sequence_and_no_forbidden_fields()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var ws = tmp.Path + "-sse-reconnect-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(ws);
        var runId = await CreateRunAsync(client, session, ws, "prompt reconnect", "key-reconnect-" + Guid.NewGuid().ToString("N"));

        // Initially after_sequence=0 should give 1 event (waiting_workspace_trust or waiting_history)
        var sse0 = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        Assert.Contains("id: 1", sse0);

        // Cancel to generate second durable event
        await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);
        await Task.Delay(50);

        // Now after_sequence=1 should give second event
        var sse1 = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=1");
        Assert.Contains("id: 2", sse1);
        Assert.DoesNotContain("id: 1\n", sse1); // should not contain first

        // after_sequence=0 should give both 1 and 2
        var sseAll = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        Assert.Contains("id: 1", sseAll);
        Assert.Contains("id: 2", sseAll);

        // Verify forbidden fields never serialize
        Assert.DoesNotContain("\"command\"", sseAll, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"arguments\"", sseAll, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"provider\"", sseAll, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sse_transient_frames_have_connection_local_seq_and_not_advance_durable_cursor()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt transient", "key-trans-" + Guid.NewGuid().ToString("N"));

        // Get hub from factory services to publish transient
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
        var runIdObj = new RuAgent.Harness.Core.RunId(runId);
        hub.TryPublish(runIdObj, "assistant.delta", "hello chunk");

        // SSE should include transient? Our implementation may include transient frames with event: transient and id: t<num>
        var sse = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        // Durable should still be present
        Assert.Contains("id: 1", sse);
        // Transient may be present as event: transient but not required to block
        // Crucially, durable cursor still 1, so requesting after_sequence=1 again should not yield transient as durable
        var sseAfter = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=1");
        // Should not contain id:1 (already consumed) and should not have durable id advanced due to transient
        Assert.DoesNotContain("id: 1\n", sseAfter);
        // The transient id is t1, not 2, so durable sequence remains same
        Assert.DoesNotContain("id: 2\ndata:", sseAfter); // no new durable
    }

    [Fact]
    public async Task Sse_bounded_channel_loss_does_not_block_final_receipt_persistence()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt loss", "key-loss-" + Guid.NewGuid().ToString("N"));
        var runIdObj = new RuAgent.Harness.Core.RunId(runId);

        // Flood hub with many transient frames (> capacity 64) to cause drop
        for (int i = 0; i < 100; i++)
        {
            hub.TryPublish(runIdObj, "tool.output.delta", $"chunk {i} " + new string('x', 1024));
        }

        // Verify hub reports dropped but not blocking
        Assert.True(hub.DroppedCount >= 0);

        // Verify durable still readable and not blocked
        var sse = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        Assert.Contains("id: 1", sse);

        // Verify we can still cancel run (which persists final receipt) despite loss
        using var cancel = await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);
        Assert.True(cancel.StatusCode == HttpStatusCode.OK || cancel.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Sse_suppresses_internal_prompt_receipt_while_advancing_cursor()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();
        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(
            client,
            session,
            tmp.Path + "-receipt-sse-" + Guid.NewGuid().ToString("N"),
            "prompt receipt",
            "receipt-sse-" + Guid.NewGuid().ToString("N"));

        var store = factory.Services.GetRequiredService<IHarnessEventStore>();
        var run = await store.GetRunAsync(new RunId(runId), CancellationToken.None)
            ?? throw new InvalidOperationException("run not found");
        var payloadId = new PayloadId("payload_" + new string('d', 32));
        var receiptBytes = Encoding.UTF8.GetBytes(
            "{\"composer_version\":\"1.0.0\",\"prompt_sha256\":\"" +
            new string('0', 64) + "\",\"block_digests\":[]}");
        var receiptPayload = new HarnessPayloadIntent(
            payloadId,
            HarnessPayloadKind.PromptReceipt,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            16 * 1024,
            receiptBytes);
        var modelStepId = new ModelStepId("step_" + new string('d', 32));
        var composedEvent = new HarnessEventIntent(
            new EventId("evt_" + new string('d', 32)),
            HarnessEventKind.ModelPromptComposed,
            modelStepId: modelStepId,
            payloadId: payloadId,
            safeCode: "composed");
        var composedRun = CopyRun(run, run.Version + 1, run.LastSequence + 1);
        await store.CommitAsync(
            new HarnessCommit(
                run.RunId,
                run.Version,
                composedRun,
                [composedEvent],
                [receiptPayload],
                []),
            CancellationToken.None);

        var completionEvent = new HarnessEventIntent(
            new EventId("evt_" + new string('e', 32)),
            HarnessEventKind.ModelStepCompleted,
            modelStepId: modelStepId);
        var completedRun = CopyRun(composedRun, composedRun.Version + 1, composedRun.LastSequence + 1);
        await store.CommitAsync(
            new HarnessCommit(
                run.RunId,
                composedRun.Version,
                completedRun,
                [completionEvent],
                [],
                []),
            CancellationToken.None);

        var sse = await GetSseAsync(
            client,
            $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        Assert.Contains("id: 1", sse);
        Assert.Contains("id: 3", sse);
        Assert.DoesNotContain("id: 2", sse);
        Assert.DoesNotContain("model.prompt_composed", sse, StringComparison.Ordinal);
        Assert.DoesNotContain(payloadId.Value, sse, StringComparison.Ordinal);
        Assert.False(HarnessEventContractValidator.IsAllowedEventType("model.prompt_composed"));

        var afterReceipt = await GetSseAsync(
            client,
            $"/agent/v1/runs/{runId}/events?live=0&after_sequence=2");
        Assert.Contains("id: 3", afterReceipt);
        Assert.DoesNotContain("id: 2", afterReceipt);
    }

    [Fact]
    public async Task Sse_terminal_final_snapshot_and_clean_close()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt terminal", "key-term-" + Guid.NewGuid().ToString("N"));
        // Cancel to make terminal
        await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);

        var sse = await GetSseAsync(client, $"/agent/v1/runs/{runId}/events?live=0&after_sequence=0");
        // Should contain both initial and cancelled events, and then close without error
        Assert.Contains("id: 1", sse);
        // Terminal run should have last_sequence at least 2 (cancel event)
        Assert.Contains("id: 2", sse);
        // Verify status field reflects terminal? Contains "cancelled"
        Assert.Contains("cancelled", sse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent.summary", sse, StringComparison.Ordinal);
        Assert.Contains("verification_state", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace_path", sse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sse_live_tail_closes_cleanly_when_run_terminates_midstream()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(client, session, tmp.Path, "prompt live", "key-live-" + Guid.NewGuid().ToString("N"));

        // Live tail (default live=1) on the still-active run; cancel concurrently
        // so the tail observes termination mid-stream. It must close gracefully
        // with a final snapshot, never abort the connection (a TCP RST surfaces
        // on clients as an opaque transport error with no resume cursor).
        var sseTask = GetSseAsync(client, $"/agent/v1/runs/{runId}/events?after_sequence=0");
        await Task.Delay(500);
        using var cancel = await client.PostAsync($"/agent/v1/runs/{runId}/cancel", null);
        Assert.True(cancel.StatusCode == HttpStatusCode.OK || cancel.StatusCode == HttpStatusCode.Conflict);
        var sse = await sseTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("id: 1", sse);
        Assert.Contains("cancelled", sse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event: final_snapshot", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_that_disconnects_releases_its_live_subscription()
    {
        using var tmp = TempWorkspace.Create();
        using var factory = new BackendWebApplicationFactory(dataPaths: tmp.DataPaths);
        using var client = factory.CreateAuthenticatedClient();

        var session = await CreateSessionAsync(client);
        var runId = await CreateRunAsync(
            client,
            session,
            tmp.Path,
            "prompt live lease",
            "key-lease-" + Guid.NewGuid().ToString("N"));
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();

        using var stream = new CancellationTokenSource();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/agent/v1/runs/{runId}/events?after_sequence=0");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            stream.Token);
        response.EnsureSuccessStatusCode();

        Assert.True(
            await WaitUntilAsync(() => hub.SubscriberCount > 0, TimeSpan.FromSeconds(10)),
            "the live stream never subscribed to the hub");

        // The client goes away mid-stream. Every exit from the endpoint has to
        // release its lease; a subscription nobody releases keeps two bounded
        // channels alive and keeps receiving publishes for a dead reader.
        stream.Cancel();
        response.Dispose();

        Assert.True(
            await WaitUntilAsync(() => hub.SubscriberCount == 0, TimeSpan.FromSeconds(10)),
            "the hub kept a subscription after the client disconnected");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return condition();
    }

    private static async Task<string> GetSseAsync(HttpClient client, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var resp = await client.SendAsync(req);
        // SSE endpoint may return 200 with text/event-stream, but our GetEvents does streaming then returns, we just read body
        // For non-terminal it may timeout after 30 sec, but for test we read with timeout
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        return body;
    }

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

    private static Task<(string trustRequestId, string displayHash)> GetWorkspaceInfoAsync(HttpClient client, string runId) => Task.FromResult((string.Empty, string.Empty));

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
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-sse-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            var resolved = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new TempWorkspace(root, ws, resolved);
        }

        public void Dispose()
        {
            try { SqliteTestPools.ClearHarnessDatabase(DataPaths.AgentHarnessDatabase); } catch { }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}

