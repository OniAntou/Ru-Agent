namespace RuAgent.Harness.Core;

public interface IPreparedModelStep
{
    long EstimatedInputTokens { get; }
    long MaxPromptTokens { get; }
    RuAgent.Protocol.Harness.ContextTokenBreakdown? ContextTokens => null;

    /// <summary>New context admitted by this exact request; commit with ModelStepStarted, before dispatch.</summary>
    string? RuntimeContextSnapshot => null;
}

public interface IModelStepRequestPreparer
{
    Task<IPreparedModelStep> PrepareAsync(ModelStepRequest request, CancellationToken cancellationToken);
}
