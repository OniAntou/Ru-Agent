using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed partial class InProcessModelStepRunnerTests
{
    [Theory]
    [InlineData("stop", null)]
    [InlineData("length", "checkpoint_incomplete")]
    [InlineData("content_filter", "checkpoint_incomplete")]
    public async Task Checkpoint_uses_the_pinned_model_once_with_bounded_output_and_preserves_usage(string finish, string? expected)
    {
        var provider = new ScriptedProvider((_, _) => Deltas(new ModelDelta(
            string.Join("\n", ContextCheckpoint.Sections.Select(section => section + "\n- prior decision")), [],
            completed: true, finishReason: finish, usage: new ModelUsage(100, 50, 150, null, null, Bootstrap().Model, true))));
        var result = await Runner(provider).SummarizeAsync(Request(), default);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Equal(100, result.Usage!.InputTokens);
        Assert.NotNull(result.PromptReceipt);
        result.PromptReceipt.ValidateAgainst(Request().PromptContext);
        Assert.Equal(1, provider.Calls);
        var sent = Assert.Single(provider.Requests);
        Assert.Equal(Bootstrap().Model, sent.Model);
        Assert.InRange(sent.MaximumOutputTokens!.Value, 1, 4096);
        Assert.Contains(ContextCheckpoint.Instruction, string.Join("\n", sent.Turns.Select(turn => turn.Content)));
    }

    [Fact]
    public async Task Checkpoint_rejects_returned_tools_without_executing_a_second_step()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(new ModelDelta(null,
            [new ModelToolFragment("call-1", "read", "{\"path\":\"README.md\"}")], true, "tool_calls")));
        var result = await Runner(provider).SummarizeAsync(Request(ContextWithReadCapability()), default);
        Assert.Equal("checkpoint_tool_rejected", result.ErrorCode);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Checkpoint_honors_pre_dispatch_cancellation()
    {
        var provider = new ScriptedProvider((_, _) => Deltas(new ModelDelta("ignored", [], true, "stop")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(provider).SummarizeAsync(Request(), cancellation.Token));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Checkpoint_keeps_provider_transients_distinct_without_automatic_retries()
    {
        var provider = new ScriptedProvider((_, _) => Throws(new ModelProviderException("rate_limited", retryable: true)));
        var error = await Assert.ThrowsAsync<ContextCheckpointException>(() => Runner(provider).SummarizeAsync(Request(), default));
        Assert.Equal("checkpoint_provider_transient", error.Code);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Checkpoint_cancels_a_hung_provider_at_its_deadline_without_retry()
    {
        var provider = new BlockingCheckpointProvider();
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
            CheckpointTimeoutOverride = TimeSpan.FromSeconds(1),
        };
        var error = await Assert.ThrowsAsync<ContextCheckpointException>(() => runner.SummarizeAsync(Request(), default));
        Assert.Equal("checkpoint_timeout", error.Code);
        Assert.Equal(1, provider.Calls);
    }

    private sealed class BlockingCheckpointProvider : IModelProvider, IModelRequestBodyProjector
    {
        public int Calls { get; private set; }
        public string ProjectRequestBody(ModelRequest request) => "{\"model\":\"model-b\",\"messages\":[]}";
        public async IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
