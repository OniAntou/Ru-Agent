using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>One picker waiting for an answer in the full-screen footer.</summary>
public sealed class PendingChoiceRequest
{
    public PendingChoiceRequest(ChoiceList list)
    {
        List = list ?? throw new ArgumentNullException(nameof(list));
    }

    public ChoiceList List { get; }

    /// <summary>Completes with the chosen value, or null on Escape/non-interactive cancel.</summary>
    public TaskCompletionSource<string?> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resolve(string? value) => Decision.TrySetResult(value);
}

/// <summary>
/// One masked secret waiting for input in the full-screen footer. The buffer never reaches
/// the transcript, the history or any log; the painter only ever reads its length for the
/// bullet row.
/// </summary>
public sealed class PendingSecretRequest
{
    public const int MaxChars = 4096;

    public PendingSecretRequest(string? providerDisplay)
    {
        ProviderDisplay = providerDisplay;
    }

    public string? ProviderDisplay { get; }

    public StringBuilder Buffer { get; } = new();

    /// <summary>Completes with the typed secret, or null on Escape/non-interactive cancel.</summary>
    public TaskCompletionSource<string?> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resolve(string? value) => Decision.TrySetResult(value);
}

/// <summary>
/// State shared between the streaming turn (which raises approvals) and the type-ahead key
/// loop (which owns the buffer and paints the pinned footer). Both sides only ever swap
/// references, and every paint is a single atomic batch on the surface gate.
/// </summary>
public sealed class TypeAheadSession
{
    private volatile ApprovalPrompt? _approval;
    private volatile PendingChoiceRequest? _choice;
    private volatile PendingSecretRequest? _secret;

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

    /// <summary>Set by the key handler when Ctrl+V asks for an image paste; the loop clears it.</summary>
    public bool PasteImageRequested { get; set; }

    /// <summary>
    /// True while a run is streaming. False between turns, when Enter hands the composer
    /// line to the idle reader instead of the type-ahead queue.
    /// </summary>
    public bool TurnActive { get; set; }

    /// <summary>
    /// The idle composer's line request. Set while the REPL waits for a prompt between
    /// turns; Enter with a non-empty buffer completes it.
    /// </summary>
    public TaskCompletionSource<string?>? IdleLine { get; set; }

    /// <summary>The approval currently waiting for an answer, if any.</summary>
    public ApprovalPrompt? Approval
    {
        get => _approval;
        set => _approval = value;
    }

    /// <summary>The picker currently waiting for an answer, if any.</summary>
    public PendingChoiceRequest? Choice
    {
        get => _choice;
        set => _choice = value;
    }

    /// <summary>The masked secret input currently waiting, if any.</summary>
    public PendingSecretRequest? Secret
    {
        get => _secret;
        set => _secret = value;
    }

    /// <summary>True while an approval owns the keyboard.</summary>
    public bool IsApprovalActive => _approval is not null;

    /// <summary>True while any interaction panel owns the keyboard and the footer.</summary>
    public bool IsPanelActive => IsApprovalActive || _choice is not null || _secret is not null;

    /// <summary>Rows the pinned footer needs in the sequential layout: status, approval block, composer frame.</summary>
    public int FooterHeight(int width) =>
        1 + (_approval?.Render(width).Count ?? 0) + ComposerBox.Height;

    public ComposerState State =>
        IsPanelActive ? ComposerState.Approval
        : Queue.HasValue ? ComposerState.Queued
        : ComposerState.Busy;

    public string Hint => IsPanelActive
        ? _choice is not null
            ? "↑↓ select · enter confirm · esc cancel"
            : _secret is not null
                ? "ctrl+v paste · esc cancel"
                : "↑↓ select · enter confirm · esc deny"
        : QueueRejected
            ? "queue full · enter retries"
            : Queue.HasValue
                ? "queued · sent when this run ends"
                : "enter queues · esc cancels · ctrl+o expands";

    /// <summary>Default top label showing workspace path and preset when no prompt is queued.</summary>
    public string? WorkspaceLabel { get; set; }

    /// <summary>Composer top label: the queued prompt, or workspace label.</summary>
    public string? TopLabel => Queue.Value is { } queued
        ? "queued · " + TuiText.Truncate(queued.ReplaceLineEndings(" "), 60)
        : WorkspaceLabel;
}
