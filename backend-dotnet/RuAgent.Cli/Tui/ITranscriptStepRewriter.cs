namespace RuAgent.Cli.Tui;

/// <summary>
/// A line-projection writer whose rows can leave the screen again. The classic console
/// path cannot unprint, so only the Terminal.Gui bridge implements this: it marks where
/// the current model step's streamed prose begins and drops those rows when the step is
/// superseded — a retried attempt, or a reply the host suppressed for recovery.
/// </summary>
public interface ITranscriptStepRewriter
{
    /// <summary>
    /// Names the model step the next streamed prose belongs to. The first fragment of a
    /// step records where that step's rows begin; the mark is replaced when a new step
    /// starts streaming.
    /// </summary>
    void BeginAssistantStep(string modelStepId);

    /// <summary>
    /// Drops the rows painted since the named step began, when it is the step still
    /// current. A null name means the current step: the durable suppression event
    /// carries no step identity, and it always follows the step that just streamed.
    /// </summary>
    void RewindAssistantStep(string? modelStepId = null);
}
