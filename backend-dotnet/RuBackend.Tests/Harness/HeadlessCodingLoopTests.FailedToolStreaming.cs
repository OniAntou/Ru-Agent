using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class HeadlessCodingLoopTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_tool_recovery_retracts_prose_on_both_live_subscriptions(bool terminal)
    {
        using var workspace = GitWorkspace.Create();
        var call = terminal
            ? ToolCall("terminal.run", JsonSerializer.Serialize(new
            {
                executable = "cmd.exe", arguments = new[] { "/d", "/c", "exit", "7" },
                working_directory = workspace.Path,
            }))
            : ToolCall("files.read", """{"path":"missing-recovery-fixture.txt"}""");
        var runner = new StreamingRecoveryRunner(call);
        using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths, modelStepRunner: runner);
        using var client = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
        await using var desktop = hub.Subscribe(new RunId(runId));
        await using var tui = hub.Subscribe(new RunId(runId));
        runner.Start.TrySetResult();

        Assert.True(await WaitForStatusAsync(client, runId,
            new HashSet<string>(StringComparer.Ordinal) { "failed", "completed" }, TimeSpan.FromSeconds(30)));
        Assert.Equal("failed", await GetStatusAsync(client, runId));
        var events = await ReadEventsAsync(client, runId);
        Assert.Contains(events, item => item.GetProperty("event_type").GetString() == "tool.execution_completed");
        Assert.Contains(events, item => item.GetProperty("safe_summary").GetString() == "unverified_after_tool_failure_advised");

        foreach (var subscription in new[] { desktop, tui })
        {
            var replies = new Dictionary<string, string>();
            var resetCount = 0;
            while (subscription.TryReadNext(out var frame))
            {
                if (frame.Kind != "assistant.delta") continue;
                var wire = new SseFrame(null, "transient",
                    JsonSerializer.Serialize(new { kind = frame.Kind, data = frame.Data }), true, frame.LocalSequence);
                Assert.True(AgentAssistantDeltaModel.TryParse(wire, out var delta));
                if (delta!.IsReset) resetCount++;
                replies[delta.ModelStepId] = delta.IsReset ? ""
                    : replies.GetValueOrDefault(delta.ModelStepId, "") + delta.Text;
            }
            Assert.Equal(1, resetCount);
            Assert.Equal("[BLOCKED] The tool returned an expected test error.",
                string.Concat(replies.Values));
            Assert.DoesNotContain("PROVISIONAL", string.Concat(replies.Values));
        }
    }

    private sealed class StreamingRecoveryRunner(AssistantStep call) : IModelStepRunner
    {
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _step;
        public async Task<ModelStepResult> RunStepAsync(ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta, CancellationToken ct)
        {
            await Start.Task.WaitAsync(ct);
            var step = Interlocked.Increment(ref _step) switch
            {
                1 => call,
                2 => FinalStep("PROVISIONAL test-error summary."),
                3 => FinalStep("[BLOCKED] The tool returned an expected test error."),
                _ => throw new InvalidOperationException("unexpected_recovery_step"),
            };
            if (!string.IsNullOrEmpty(step.Text))
                await publishDelta(new AssistantDelta(request.ModelStepId, 1, step.Text));
            return new ModelStepResult(step, SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt);
        }
    }
}
