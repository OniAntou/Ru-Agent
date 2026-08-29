using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class WorkerV2StepServiceTests
{
    [Fact]
    public async Task Step_service_composes_once_and_returns_one_done_with_receipt()
    {
        var provider = new FakeModelProvider(
            new ModelDelta("xin ", Array.Empty<ModelToolFragment>(), false, null),
            new ModelDelta("chao anh", Array.Empty<ModelToolFragment>(), true, "stop"));
        var result = await new WorkerV2StepService(provider).RunAsync(StartPayload(), CancellationToken.None);

        Assert.Single(provider.Requests);
        Assert.Equal("xin chao anh", result.Text);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("1.0.0", result.PromptReceipt.ComposerVersion);
        Assert.Equal("## ru.persona", provider.Requests[0].SystemPrompt[..13]);
    }

    [Fact]
    public async Task Step_service_accumulates_one_tool_call_without_visible_arguments()
    {
        var provider = new FakeModelProvider(
            new ModelDelta(null, new[] { new ModelToolFragment("call-1", "files.read", "{\"path\":\"README.md\"}") }, true, "tool_calls"));

        var result = await new WorkerV2StepService(provider).RunAsync(StartPayload(includeTool: true), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Single(result.ToolCalls);
        Assert.Equal("files.read", result.ToolCalls[0].ToolId);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.DoesNotContain("README.md", result.PromptReceipt.PromptSha256, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_service_observes_cancellation_and_does_not_release_a_running_provider_early()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new BlockingProvider();
        var task = new WorkerV2StepService(provider).RunAsync(StartPayload(), cancellation.Token);
        await provider.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(provider.Cancelled.Task.IsCompleted);
    }

    private static JsonObject StartPayload(bool includeTool = false)
    {
        var context = WorkerV2PromptCodec.WriteContext(SystemPromptContext.Empty);
        var settings = new JsonObject();
        if (includeTool)
        {
            settings["model_tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool_id"] = "files.read",
                    ["tool_version"] = "1.0.0",
                    ["scopes"] = new JsonArray("workspace.read"),
                    ["availability"] = "executable",
                    ["model_enabled"] = true,
                    ["capability_class"] = "tool",
                    ["verification_required"] = false,
                },
            };
        }
        return new JsonObject
        {
            ["prompt"] = "xin chào",
            ["turns"] = new JsonArray(),
            ["system_prompt_context"] = context,
            ["settings"] = settings,
        };
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelDelta> _deltas;
        public List<ModelRequest> Requests { get; } = new();

        public FakeModelProvider(params ModelDelta[] deltas) => _deltas = deltas;

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var delta in _deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class BlockingProvider : IModelProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.SetResult();
                throw;
            }

            yield break;
        }
    }
}
