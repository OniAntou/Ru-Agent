using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// A step that fails while the host prepares its request must reach the run under the code
/// that names the real problem. Every such failure used to arrive as prompt_contract_invalid,
/// including a conversation whose replayed reasoning was too large, which sent the reader
/// looking for a prompt problem that did not exist.
/// </summary>
public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData("model_history_too_large")]
    [InlineData("provider_continuation_invalid")]
    [InlineData("provider_request_projection_unavailable")]
    public async Task A_named_preparation_failure_keeps_its_code(string code)
    {
        var result = await RunPreparationThatThrows(new StableModelStepError(code));

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("Not A Code")]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("0starts_with_digit")]
    public async Task A_preparation_code_with_the_wrong_shape_is_not_forwarded(string code)
    {
        var result = await RunPreparationThatThrows(new StableModelStepError(code));

        Assert.Equal("prompt_contract_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task An_unclassified_preparation_failure_is_still_a_prompt_contract_failure()
    {
        var result = await RunPreparationThatThrows(new InvalidOperationException("anything else"));

        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal("prompt_contract_invalid", result.ErrorCode);
    }

    private static async Task<AgentRunStepResult> RunPreparationThatThrows(Exception failure)
    {
        var model = new PreparationThrowingModelStepRunner(failure);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        return await Coordinator(
                model,
                new FakeToolGateway(),
                store,
                contextLimit: new StaticModelContextLimitProvider(200_000))
            .RunOneStepAsync(RunId(), CancellationToken.None);
    }

    private sealed class PreparationThrowingModelStepRunner(Exception failure) : IModelStepRunner, IModelStepRequestPreparer
    {
        public Task<IPreparedModelStep> PrepareAsync(ModelStepRequest request, CancellationToken cancellationToken) =>
            throw failure;

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the step must not run after a failed preparation");
    }
}
