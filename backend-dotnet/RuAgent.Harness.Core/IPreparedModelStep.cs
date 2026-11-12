namespace RuAgent.Harness.Core;

public interface IPreparedModelStep
{
    long EstimatedInputTokens { get; }
    long MaxPromptTokens { get; }
    RuAgent.Protocol.Harness.ContextTokenBreakdown? ContextTokens => null;

    /// <summary>
    /// How full the request's bounded history window is, from 0 to 1: the larger of its
    /// message count and its byte size against the window the runner fits history into.
    /// The runner drops the oldest history to stay inside that window, so a full window
    /// on a large-context model loses context silently unless it is compacted first.
    /// </summary>
    double HistoryWindowFill => 0;

    /// <summary>New context admitted by this exact request; commit with ModelStepStarted, before dispatch.</summary>
    string? RuntimeContextSnapshot => null;
}

public interface IModelStepRequestPreparer
{
    Task<IPreparedModelStep> PrepareAsync(ModelStepRequest request, CancellationToken cancellationToken);
}
