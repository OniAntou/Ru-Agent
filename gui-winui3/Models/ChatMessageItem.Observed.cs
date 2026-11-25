namespace RuApp.Models;

public partial class ChatMessageItem
{
    /// <summary>
    /// A row drawn from another run's replay (a subagent's prompt and work) with the main
    /// transcript's templates. It is observation only: it belongs to no session the user can
    /// rewind, and its changes are reviewed in the Subagents panel, not the turn summary.
    /// </summary>
    public bool IsObserved { get; set; }

    /// <summary>Whether "Rewind to Here" applies: only rows of the user's own session.</summary>
    public bool CanRewind => !IsObserved;

    /// <summary>
    /// Ends the turn at a recorded instant instead of "now", so a run read after it ended
    /// says how long it worked rather than how long ago it was opened.
    /// </summary>
    internal void SettleAt(DateTime completedAt)
    {
        IsStreaming = false;
        if (_completedAt == completedAt) return;
        _completedAt = completedAt;
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(ProcessHeader));
    }
}
