using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Business;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// A durable approval request is committed after the last transient activity frame
/// of the turn. The live event stream must deliver it on its own instead of waiting
/// for unrelated live activity to arrive, otherwise an interactive client never
/// learns that a run is parked and never offers the approval surface.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class AgentApprovalLiveDeliveryTests
{
    [Fact]
    public async Task Approval_requested_reaches_a_live_stream_without_further_live_activity()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-approval-live-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "ws");
        var external = Path.Combine(root, "external");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(dataRoot);
        var externalFile = Path.Combine(external, "note.txt");
        await File.WriteAllTextAsync(externalFile, "external content");

        BackendWebApplicationFactory? factory = null;
        var dataPaths = DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new GatedRunner(
                gate,
                ToolCall("files.read", JsonSerializer.Serialize(new { path = externalFile })),
                FinalStep("done"));

            factory = new BackendWebApplicationFactory(
                dataPaths: dataPaths,
                modelStepRunner: runner);
            using var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var runId = await CreateRunAndTrustAsync(client, sessionId, workspace, "live approval delivery");

            // Attach the live stream (live=1 by default) before the tool decision runs,
            // then wait until the endpoint has actually subscribed to the live hub.
            var readTask = ReadUntilAsync(client, runId, "\"event_type\":\"approval.requested\"", TimeSpan.FromSeconds(10));
            var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
            Assert.True(
                await WaitUntilAsync(() => hub.SubscriberCount > 0, TimeSpan.FromSeconds(5)),
                "the live event stream never subscribed");

            gate.SetResult();

            var (found, received) = await readTask;
            Assert.True(
                found,
                "approval.requested never reached the live event stream. Received: " + received);
            Assert.Contains("approval_id", received, StringComparison.Ordinal);
        }
        finally
        {
            factory?.Dispose();
            try { SqliteTestPools.ClearHarnessDatabase(dataPaths.AgentHarnessDatabase); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<(bool Found, string Received)> ReadUntilAsync(
        HttpClient client,
        string runId,
        string marker,
        TimeSpan timeout)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/agent/v1/runs/{runId}/events?after_sequence=0");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var received = new StringBuilder();
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cts.Token);
                if (read == 0)
                {
                    break;
                }

                received.Append(buffer, 0, read);
                if (received.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    return (true, received.ToString());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The marker never arrived within the window; report what was seen.
        }

        return (received.ToString().Contains(marker, StringComparison.Ordinal), received.ToString());
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

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/sessions/new", null);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static async Task<string> CreateRunAndTrustAsync(
        HttpClient client,
        string sessionId,
        string workspacePath,
        string prompt)
    {
        using var runResponse = await client.PostAsJsonAsync("/agent/v1/runs", new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt,
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N"),
        });
        runResponse.EnsureSuccessStatusCode();
        var created = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("run_id").GetString()!;
        if (created.TryGetProperty("trust_request_id", out var trustId)
            && trustId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(trustId.GetString()))
        {
            using var trustResponse = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/trust",
                new
                {
                    trust_request_id = trustId.GetString(),
                    display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
                    decision = "approve",
                });
            trustResponse.EnsureSuccessStatusCode();
        }

        return runId;
    }

    private sealed class GatedRunner(TaskCompletionSource gate, params AssistantStep[] steps) : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps = new(steps);
        private int _calls;

        public async Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var step = _steps.Count > 0 ? _steps.Dequeue() : FinalStep("done");
            return new ModelStepResult(
                step,
                RuAgent.Harness.Core.Prompting.SystemPromptComposer
                    .Compose(request.PromptContext, nativeToolCalling: true)
                    .Receipt);
        }
    }

    private static AssistantStep ToolCall(string toolId, string argumentsJson)
    {
        var node = JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        if (toolId == "files.read")
        {
            // read no longer carries an op discriminator.
            toolId = "read";
        }

        return new AssistantStep(
            null,
            new[]
            {
                new ModelToolCall(
                    "provider_call_1",
                    toolId,
                    "1.0.0",
                    JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);
    }

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);
}
