namespace RuAgent.Cli.Tui;

/// <summary>
/// State shared between the streaming turn (which raises approvals) and the type-ahead key
/// loop (which owns the buffer and paints the pinned footer). Both sides only ever swap
/// references, and every paint is a single atomic batch on the surface gate.
/// </summary>
public sealed class TypeAheadSession
{
    private volatile ApprovalPrompt? _approval;

    public TypeAheadSession(PendingPromptSlot queue)
    {
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    /// <summary>One-slot queue for prompts typed while the agent is working.</summary>
    public PendingPromptSlot Queue { get; }

    /// <summary>Buffer being edited in the pinned composer.</summary>
    public string Buffer { get; set; } = string.Empty;

    /// <summary>Set when Enter could not queue because the slot already holds a prompt.</summary>
    public bool QueueRejected { get; set; }

    /// <summary>The approval currently waiting for an answer, if any.</summary>
    public ApprovalPrompt? Approval
    {
        get => _approval;
        set => _approval = value;
    }

    /// <summary>True while an approval owns the keyboard.</summary>
    public bool IsApprovalActive => _approval is not null;

    /// <summary>Rows the pinned footer needs: status row, approval block, composer frame.</summary>
    public int FooterHeight(int width) =>
        1 + (_approval?.Render(width).Count ?? 0) + ComposerBox.Height;

    public ComposerState State =>
        IsApprovalActive ? ComposerState.Approval
        : Queue.HasValue ? ComposerState.Queued
        : ComposerState.Busy;

    public string Hint => IsApprovalActive
        ? "↑↓ select · enter confirm · esc deny"
        : QueueRejected
            ? "queue full · enter retries"
            : Queue.HasValue
                ? "queued · sent when this run ends"
                : "enter queues · esc cancels · ctrl+o expands";

    /// <summary>Composer top label: the queued prompt, or nothing.</summary>
    public string? TopLabel => Queue.Value is { } queued
        ? "queued · " + TuiText.Truncate(queued.ReplaceLineEndings(" "), 60)
        : null;
}
