using RuAgent.Cli.Tui;
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
/// Steering (ADR 0056): Enter on a plain prompt while a turn runs sends it into that run
/// instead of queueing it. Tab still queues. A steer the run can no longer take becomes the
/// next queued turn, and a refused one goes back to the prompt, so typed text is never lost.
/// </summary>
public partial class ChatSessionEngine
{
    internal const string SteerWaitingHint = "message waiting for Ru's next step · esc sends it now";

    private Func<string, Task<SteerResult>>? _steerHandler;

    internal void SetSteerHandler(Func<string, Task<SteerResult>> handler) => _steerHandler = handler;

    private bool ShouldSteer(string line, bool hasAttachments, bool retainedContinuation)
    {
        if (_steerHandler is null || hasAttachments || retainedContinuation) return false;
        var text = line.Trim();
        if (text.Length is 0 or > AgentSteerLimits.MaxCharacters || text.StartsWith('/')) return false;
        lock (_queueGate)
        {
            // A paused queue or an open local surface (picker, report) is not a working run.
            return _busy && !_queuePaused && !_localCommandActive && _taskControlCancellation is null;
        }
    }

    private async Task SteerAsync(string text)
    {
        SteerResult result;
        try
        {
            result = await _steerHandler!(text).ConfigureAwait(false);
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
            switch (result.Attempt)
            {
                case SteerAttempt.Sent:
                    // The transcript writes the message where the run reads it; until then
                    // the hint row says it is waiting and how to send it now.
                    _view.ShowFeedback(SteerWaitingHint);
                    break;
                case SteerAttempt.NotSteerable:
                    // The run ended while the steer was in flight: the text is the next turn.
                    Submit(forceQueue: true, steerFallback: text);
                    break;
                default:
                    RestoreToPrompt(text);
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

    /// <summary>
    /// Messages the run ended without reading were meant for now: they become the next turn,
    /// ahead of any queued prompt. Esc therefore also sends a waiting message at once.
    /// </summary>
    internal void SendReturnedSteers(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return;
        Dispatch(() => Submit(forceQueue: true, steerFallback: string.Join("\n\n", texts)));
    }
}
