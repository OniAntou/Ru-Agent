namespace RuAgent.Harness.Core;

/// <summary>
/// The presentation-safe form of a model's final answer. Private protocol markers
/// never cross the persistence or authenticated-client boundary.
/// </summary>
public sealed record AgentReplyPresentation
{
    public AgentReplyPresentation(
        string visibleText,
        string? emotion = null)
    {
        if (string.IsNullOrWhiteSpace(visibleText) || visibleText.Length > 1024 * 1024)
        {
            throw new ArgumentException("Visible reply text is empty or too large.", nameof(visibleText));
        }

        if (emotion is not null
            && emotion is not ("none" or "joy" or "sad" or "angry" or "surprised" or "shy"))
        {
            throw new ArgumentException("Emotion is not supported.", nameof(emotion));
        }

        VisibleText = visibleText;
        Emotion = emotion;
    }

    public string VisibleText { get; }

    public string? Emotion { get; }
}

public interface IAgentReplyPolicy
{
    ValueTask<AgentReplyPresentation> PrepareAsync(
        AgentRun run,
        string rawText,
        CancellationToken cancellationToken);
}

public interface IAgentReplyEffectSink
{
    ValueTask ApplyAsync(
        AgentRun run,
        AgentReplyPresentation reply,
        ModelStepId modelStepId,
        CancellationToken cancellationToken);
}

public sealed class PassthroughAgentReplyPolicy : IAgentReplyPolicy
{
    public ValueTask<AgentReplyPresentation> PrepareAsync(
        AgentRun run,
        string rawText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(rawText);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentReplyPresentation(rawText));
    }
}

public sealed class NoopAgentReplyEffectSink : IAgentReplyEffectSink
{
    public ValueTask ApplyAsync(
        AgentRun run,
        AgentReplyPresentation reply,
        ModelStepId modelStepId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(reply);
        _ = modelStepId.Value;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
