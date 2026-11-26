using RuAgent.Cli.Attachments;
using RuAgent.Cli.Tui;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Chat;

internal enum SteerAttempt
{
    /// <summary>The Host holds the steer; the model reads it at its next step.</summary>
    Sent,

    /// <summary>The run is no longer working: the text becomes the next turn instead.</summary>
    NotSteerable,

    /// <summary>The Host refused it (a prompt hook, a full steer queue, a transport error).</summary>
    Refused,
}

internal readonly record struct SteerResult(SteerAttempt Attempt, string? Message = null);

/// <summary>
/// Steering (ADR 0056): Enter on a prompt while a turn runs sends it, with its files and
/// images, into that run instead of queueing it. Tab and commands still queue. A steer the
/// run can no longer take becomes the next queued turn, and a refused one goes back to the
/// prompt with its attachments, so nothing the user wrote or attached is lost.
/// </summary>
public partial class ChatSessionEngine
{
    internal const string SteerWaitingHint = "message waiting for Ru's next step";

    private Func<string, ChatSubmissionSnapshot, Task<SteerResult>>? _steerHandler;
    private Action<ChatSubmissionSnapshot>? _submissionReturned;

    /// <summary>Sends a steer: its composed text and the submission whose images it carries.</summary>
    internal void SetSteerHandler(Func<string, ChatSubmissionSnapshot, Task<SteerResult>> handler) => _steerHandler = handler;

    internal void SetSteerHandler(Func<string, Task<SteerResult>> handler) =>
        _steerHandler = (text, _) => handler(text);

    /// <summary>A one-line hint from the session, shown on the prompt's hint row.</summary>
    internal void ShowFeedback(string text, bool isError) => Dispatch(() => _view.ShowFeedback(text, isError));

    /// <summary>Puts a steer's files back in the pending list when the steer was not sent.</summary>
    internal void SetSubmissionReturnHandler(Action<ChatSubmissionSnapshot> returned) => _submissionReturned = returned;

    /// <summary>
    /// The text a steer carries, or null when this submit is a new turn. Text and code files
    /// travel inside the prompt text (the envelope a turn uses) and images beside it, so a
    /// steer carries whatever a turn can. Only a command needs a turn.
    /// </summary>
    private string? SteerText(string line, ChatSubmissionSnapshot submission, bool retainedContinuation)
    {
        if (_steerHandler is null || retainedContinuation) return null;
        var typed = line.Trim();
        if (typed.StartsWith('/') || typed.Length == 0 && submission.Texts.IsEmpty && submission.Images.IsEmpty) return null;
        var text = AttachedPrompt.Compose(typed, submission.Texts);
        if (text.Length > AgentSteerLimits.MaxCharacters || submission.Images.Length > AgentSteerLimits.MaxImages) return null;
        lock (_queueGate)
        {
            // A paused queue or an open local surface (picker, report) is not a working run.
            return _busy && !_queuePaused && !_localCommandActive && _taskControlCancellation is null ? text : null;
        }
    }

    /// <summary>
    /// Sends one steer. Its files leave the pending list while it is in flight, so a second
    /// Enter cannot send them twice; any outcome but a sent steer gives them back.
    /// </summary>
    private async Task SteerAsync(string typed, string text, ChatSubmissionSnapshot submission)
    {
        SteerResult result;
        try
        {
            result = await _steerHandler!(text, submission).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            TuiDebug.Log("steer", $"failed: {error.GetType().Name}");
            result = new SteerResult(SteerAttempt.Refused, "steer failed · your text is back in the prompt");
        }
        catch (OperationCanceledException)
        {
            result = new SteerResult(SteerAttempt.Refused, "steer cancelled · your text is back in the prompt");
        }

        Dispatch(() =>
        {
            if (result.Attempt != SteerAttempt.Sent) _submissionReturned?.Invoke(submission);
            switch (result.Attempt)
            {
                case SteerAttempt.Sent:
                    // The transcript writes the message where the run reads it; until then
                    // the hint row says it is waiting and how to send it now.
                    _view.ShowFeedback(SteerWaitingHint);
                    break;
                case SteerAttempt.NotSteerable:
                    // The run ended while the steer was in flight: the text and its files,
                    // pending again, are the next turn.
                    Submit(forceQueue: true, steerFallback: typed);
                    break;
                default:
                    if (typed.Length > 0) RestoreToPrompt(typed);
                    _view.ShowFeedback(result.Message ?? "steer refused · your text is back in the prompt", true);
                    break;
            }
        });
    }

    /// <summary>
    /// Puts undelivered text back where the user can send it again. A prompt the user has
    /// already started typing keeps its text; the returned steer goes in front of it.
    /// </summary>
    internal void RestoreToPrompt(string text)
    {
        var current = _view.InputText;
        _view.InputText = string.IsNullOrEmpty(current) ? text : text + "\n" + current;
    }
}
