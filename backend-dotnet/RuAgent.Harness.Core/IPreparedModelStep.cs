namespace RuAgent.Harness.Core;

public interface IPreparedModelStep
{
    long EstimatedInputTokens { get; }
    long MaxPromptTokens { get; }
}

public interface IModelStepRequestPreparer
{
    Task<IPreparedModelStep> PrepareAsync(ModelStepRequest request, CancellationToken cancellationToken);
}
