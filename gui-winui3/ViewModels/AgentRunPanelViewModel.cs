using CommunityToolkit.Mvvm.ComponentModel;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>An approval the run is waiting on, as the approval card shows it.</summary>
public sealed record AgentApprovalPrompt(
    string ApprovalId,
    string Summary,
    string? TerminalCommand,
    bool CanAllowSessionDirectory,
    bool CanRememberAllowAlways,
    bool CanRememberForSession);

/// <summary>A question the run is waiting on, as the question card shows it.</summary>
public sealed record AgentQuestionPrompt(string InputId, string Question, IReadOnlyList<string> Options);

/// <summary>
/// Everything the run panel is derived from: the inline-panel policy input plus the
/// rendered approval and question, already resolved by the caller.
/// </summary>
internal sealed record AgentRunPanelInput(
    AgentInlinePanelInput Panel,
    AgentApprovalPrompt? Approval,
    AgentQuestionPrompt? Question);

/// <summary>
/// The composer's run surface: the status line, Stop, the send/stop face, the approval
/// and question cards, and the guard that keeps one workspace-trust prompt per run.
/// </summary>
/// <remarks>
/// The rules stay in <see cref="AgentInlinePanelPolicy"/>; this owns the state around
/// them that used to live in view fields. A composer-local message (a rejected file, a
/// queue notice) has no run behind it, so it overrides the policy's status text until it
/// is cleared, instead of being erased by the next refresh.
/// </remarks>
public sealed partial class AgentRunPanelViewModel : ObservableObject
{
    /// <summary>
    /// How long a trust prompt that could not open (another dialog was showing) waits
    /// before the next refresh may offer it again.
    /// </summary>
    internal static readonly TimeSpan TrustPromptRetryDelay = TimeSpan.FromSeconds(1);

    private readonly ComposerLocalStatus _localStatus = new();
    private string _policyStatus = string.Empty;
    private bool _policyShowsPanel;
    private string? _focusedApprovalId;
    private string? _focusedInputId;

    private object? _trustController;
    private string? _trustRunId;
    private string? _trustSessionId;
    private string? _handledTrustRunId;
    private string? _handledTrustSessionId;
    private DateTimeOffset _trustRetryAfter = DateTimeOffset.MinValue;

    /// <summary>A new approval arrived; the view brings it into view and focuses its decision.</summary>
    internal event Action? ApprovalArrived;

    /// <summary>A new question arrived; the view brings it into view and focuses its answer box.</summary>
    internal event Action? QuestionArrived;

    /// <summary>The run waits on workspace trust and no prompt for it is open or answered.</summary>
    internal event Action? TrustPromptRequested;

    [ObservableProperty] public partial bool IsPanelVisible { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    public partial string StatusText { get; private set; } = string.Empty;

    public bool IsStatusVisible => !string.IsNullOrWhiteSpace(StatusText);

    [ObservableProperty] public partial bool IsStopVisible { get; private set; }

    /// <summary>Approve/deny, answer and Stop are disabled while one of them is being submitted.</summary>
    [ObservableProperty] public partial bool IsInteractionEnabled { get; private set; } = true;

    /// <summary>Whether Enter queues (a run is in hand) rather than sends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendGlyph), nameof(SendName), nameof(ComposerPlaceholder))]
    public partial bool IsRunning { get; private set; }

    public string SendGlyph => IsRunning ? AgentComposerPresentation.StopGlyph : AgentComposerPresentation.SendGlyph;

    public string SendName => IsRunning ? AgentComposerPresentation.StopName : AgentComposerPresentation.SendName;

    /// <summary>Says which of the two things Enter will do, so a second thought is never swallowed.</summary>
    public string ComposerPlaceholder => IsRunning
        ? AgentComposerPresentation.QueuePlaceholder
        : AgentComposerPresentation.MessagePlaceholder;

    [ObservableProperty] public partial AgentApprovalPrompt? Approval { get; private set; }

    [ObservableProperty] public partial AgentQuestionPrompt? Question { get; private set; }

    /// <summary>The composer-local message, empty when none is held.</summary>
    internal string LocalStatus => _localStatus.Text;

    internal bool IsTrustPromptOpen => _trustRunId is not null;

    /// <summary>Holds a composer-local message until it is replaced or cleared.</summary>
    internal void ShowLocalStatus(string status)
    {
        _localStatus.Set(status);
        Project();
    }

    internal void ClearLocalStatus()
    {
        _localStatus.Clear();
        Project();
    }

    /// <summary>The send path flips the face before the controller reports a run.</summary>
    internal void SetRunning(bool isRunning) => IsRunning = isRunning;

    /// <summary>Applies the panel policy and the pending prompts.</summary>
    internal void Evaluate(AgentRunPanelInput input)
    {
        var state = AgentInlinePanelPolicy.Evaluate(input.Panel);
        IsRunning = state.IsRunning;
        _policyShowsPanel = state.ShowInlinePanel;
        _policyStatus = state.ShowInlinePanel ? state.StatusText : string.Empty;
        IsStopVisible = state.ShowStop;
        IsInteractionEnabled = !state.SuppressInteraction;

        var approval = state.ShowApproval ? input.Approval : null;
        Approval = approval;
        if (approval is null)
        {
            _focusedApprovalId = null;
        }
        else if (!string.Equals(_focusedApprovalId, approval.ApprovalId, StringComparison.Ordinal))
        {
            // Once per approval: the projection reruns on every refresh, and re-grabbing
            // the keyboard would make the rest of the card unusable.
            _focusedApprovalId = approval.ApprovalId;
            ApprovalArrived?.Invoke();
        }

        var question = state.ShowQuestion ? input.Question : null;
        Question = question;
        if (question is null)
        {
            _focusedInputId = null;
        }
        else if (!string.Equals(_focusedInputId, question.InputId, StringComparison.Ordinal))
        {
            _focusedInputId = question.InputId;
            QuestionArrived?.Invoke();
        }

        Project();
        if (state.ShowWorkspaceTrust) TrustPromptRequested?.Invoke();
    }

    private void Project()
    {
        StatusText = _localStatus.IsVisible ? _localStatus.Text : _policyStatus;
        IsPanelVisible = _policyShowsPanel || _localStatus.IsVisible;
    }

    /// <summary>Forgets the run surface when the session or run in hand is replaced.</summary>
    internal void Reset()
    {
        ClearTrust();
        _localStatus.Clear();
        _policyShowsPanel = false;
        _policyStatus = string.Empty;
        _focusedApprovalId = null;
        _focusedInputId = null;
        Approval = null;
        Question = null;
        IsStopVisible = false;
        IsInteractionEnabled = true;
        IsRunning = false;
        Project();
    }

    /// <summary>
    /// Drops the trust guard when the controller, run or session it was taken for is no
    /// longer the one in hand, so a later run can be prompted.
    /// </summary>
    internal void ForgetTrustIfRunChanged(object? controller, string? runId, string? runSessionId, string activeSessionId)
    {
        bool hasOpen = _trustRunId is not null;
        bool hasHandled = _handledTrustRunId is not null;
        if (!hasOpen && !hasHandled) return;

        bool sameController = ReferenceEquals(_trustController, controller);
        bool sameRun = runId is not null
            && ((hasOpen && string.Equals(runId, _trustRunId, StringComparison.Ordinal))
                || (hasHandled && string.Equals(runId, _handledTrustRunId, StringComparison.Ordinal)));
        bool sameSession = runSessionId is not null
            && string.Equals(runSessionId, activeSessionId, StringComparison.Ordinal)
            && ((hasOpen && string.Equals(runSessionId, _trustSessionId, StringComparison.Ordinal))
                || (hasHandled && string.Equals(runSessionId, _handledTrustSessionId, StringComparison.Ordinal)));
        if (!sameController || !sameRun || !sameSession) ClearTrust();
    }

    /// <summary>
    /// Takes the trust guard for a run. False while a prompt is open, during the retry
    /// cooldown, or when this run's prompt was already answered.
    /// </summary>
    internal bool TryBeginTrustPrompt(object controller, string runId, string sessionId, DateTimeOffset now)
    {
        if (_trustRunId is not null || now < _trustRetryAfter) return false;
        if (_handledTrustRunId is not null
            && ReferenceEquals(_trustController, controller)
            && string.Equals(_handledTrustRunId, runId, StringComparison.Ordinal)
            && string.Equals(_handledTrustSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        _trustRetryAfter = DateTimeOffset.MinValue;
        _trustRunId = runId;
        _trustSessionId = sessionId;
        _trustController = controller;
        return true;
    }

    /// <summary>Whether this prompt is still the one the guard is held for.</summary>
    internal bool OwnsTrustPrompt(object controller, string runId, string sessionId) =>
        ReferenceEquals(_trustController, controller)
        && string.Equals(_trustRunId, runId, StringComparison.Ordinal)
        && string.Equals(_trustSessionId, sessionId, StringComparison.Ordinal);

    /// <summary>
    /// Releases the guard after a prompt. Only an answered prompt (or a failure other
    /// than a dialog collision) counts as handled; a prompt that never opened is offered
    /// again after <see cref="TrustPromptRetryDelay"/>, because the run is still parked.
    /// Returns whether the caller still owned the guard.
    /// </summary>
    internal bool EndTrustPrompt(object controller, string runId, string sessionId, bool couldNotOpen, DateTimeOffset now)
    {
        if (couldNotOpen) _trustRetryAfter = now + TrustPromptRetryDelay;
        bool owned = OwnsTrustPrompt(controller, runId, sessionId);
        if (owned)
        {
            if (!couldNotOpen)
            {
                _handledTrustRunId = runId;
                _handledTrustSessionId = sessionId;
            }

            _trustRunId = null;
            _trustSessionId = null;
        }

        return owned;
    }

    private void ClearTrust()
    {
        _trustRunId = null;
        _trustSessionId = null;
        _handledTrustRunId = null;
        _handledTrustSessionId = null;
        _trustController = null;
        _trustRetryAfter = DateTimeOffset.MinValue;
    }
}
