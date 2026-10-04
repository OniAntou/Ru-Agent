using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

public interface IModelStepRunner
{
    Task<ModelStepResult> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken);
}

public interface IProviderBackedModelStepRunner : IModelStepRunner
{
}
