namespace RuAgent.Harness.Core;

public interface IModelStepRunner
{
    Task<AssistantStep> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken);
}
