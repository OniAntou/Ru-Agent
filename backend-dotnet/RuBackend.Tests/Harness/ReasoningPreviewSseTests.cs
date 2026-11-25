using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Business;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The live stream carries the running thinking row's line end to end: each changed
/// summary line reaches the SSE subscriber as its own `agent.reasoning_preview` frame.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class ReasoningPreviewSseTests
{
    [Fact]
    public async Task Every_changed_thinking_line_reaches_the_live_stream()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-reasoning-preview-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "ws");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(dataRoot);
        var dataPaths = DataPaths.Resolve(root, root, root, dataRoot, Path.Combine(root, "logs"));
        BackendWebApplicationFactory? factory = null;
        try
        {
            factory = new BackendWebApplicationFactory(
                dataPaths: dataPaths,
                modelStepRunner: new PacedReasoningRunner());
            using var client = factory.CreateAuthenticatedClient();
            var sessionId = await CreateSessionAsync(client);
            var runId = await CreateRunAndTrustAsync(client, sessionId, workspace, "reasoning preview delivery");

            var (received, completed) = await ReadAllAsync(client, runId, TimeSpan.FromSeconds(20));

            Assert.True(completed, "the run's live stream did not finish. Received: " + received);
            Assert.Contains("\"kind\":\"agent.reasoning_preview\"", received, StringComparison.Ordinal);
            Assert.True(received.Contains("First line", StringComparison.Ordinal), received);
            Assert.True(received.Contains("Second paragraph", StringComparison.Ordinal), received);
        }
        finally
        {
            factory?.Dispose();
            try { SqliteTestPools.ClearHarnessDatabase(dataPaths.AgentHarnessDatabase); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<(string Received, bool Completed)> ReadAllAsync(
        HttpClient client,
        string runId,
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
                    return (received.ToString(), true);
                }

                received.Append(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            return (received.ToString(), false);
        }
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

    /// <summary>Streams a reasoning line, waits, then starts a second paragraph.</summary>
    private sealed class PacedReasoningRunner : IModelStepRunner
    {
        public async Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            await publishDelta(new AssistantDelta(
                request.ModelStepId, 1, "First line\n", kind: AssistantDeltaKind.Reasoning));
            await Task.Delay(700, cancellationToken);
            await publishDelta(new AssistantDelta(
                request.ModelStepId, 2, "\nSecond paragraph\n", kind: AssistantDeltaKind.Reasoning));
            await Task.Delay(700, cancellationToken);
            var step = new AssistantStep("done", [], AssistantFinishReason.Stop, 0);
            return new ModelStepResult(step, SystemPromptComposer.Compose(request.PromptContext).Receipt);
        }
    }
}
