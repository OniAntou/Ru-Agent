namespace RuApp.Services;

internal enum RewindEligibility
{
    Allowed,
    HistoryLoading,
    ActionPending,
    RunPending,
    RowUnavailable,
}

/// <summary>Coordinates history actions whose rendering belongs to ChatView.</summary>
internal sealed class SessionHistoryPresenter
{
    private const int ActionPending = 1;
    private const int RewindInProgress = 2;
    private int _activityState;

    internal bool IsActionPending => (Volatile.Read(ref _activityState) & ActionPending) != 0;
    internal bool IsRewindInProgress => (Volatile.Read(ref _activityState) & RewindInProgress) != 0;

    internal bool TryBeginAction()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activityState);
            if ((current & (ActionPending | RewindInProgress)) != 0) return false;
            if (Interlocked.CompareExchange(ref _activityState, current | ActionPending, current) == current) return true;
        }
    }

    internal void EndAction() => Interlocked.And(ref _activityState, ~ActionPending);

    internal void SetActionPending(bool pending)
    {
        while (true)
        {
            int current = Volatile.Read(ref _activityState);
            int updated = pending ? current | ActionPending : current & ~ActionPending;
            if (Interlocked.CompareExchange(ref _activityState, updated, current) == current) return;
        }
    }

    internal async Task RunApprovalAsync(
        bool approved,
        string? scope,
        Func<AgentApprovalOperation, Task> submit,
        Func<bool> ownsController,
        Action<string> showStatus,
        Action refreshState,
        Action startMonitor,
        Action<Exception> showError,
        Action clearStatus)
    {
        var operation = ResolveApproval(approved, scope);
        var status = approved ? "Submitting approval…" : "Denying approval…";
        await RunInteractionAsync(operation, status, submit, ownsController, showStatus, refreshState, startMonitor, showError, clearStatus)
            .ConfigureAwait(true);
    }

    internal async Task RunInputAsync(
        string answer,
        Func<string, Task> submit,
        Func<bool> ownsController,
        Action<string> showStatus,
        Action refreshState,
        Action startMonitor,
        Action<Exception> showError,
        Action clearStatus)
    {
        ArgumentNullException.ThrowIfNull(answer);
        await RunInteractionAsync(answer, "Submitting answer…", submit, ownsController, showStatus, refreshState, startMonitor, showError, clearStatus)
            .ConfigureAwait(true);
    }

    private async Task RunInteractionAsync<TRequest>(
        TRequest request,
        string status,
        Func<TRequest, Task> submit,
        Func<bool> ownsController,
        Action<string> showStatus,
        Action refreshState,
        Action startMonitor,
        Action<Exception> showError,
        Action clearStatus)
    {
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(ownsController);
        ArgumentNullException.ThrowIfNull(showStatus);
        ArgumentNullException.ThrowIfNull(refreshState);
        ArgumentNullException.ThrowIfNull(startMonitor);
        ArgumentNullException.ThrowIfNull(showError);
        ArgumentNullException.ThrowIfNull(clearStatus);
        if (!TryBeginAction()) return;
        if (!ownsController())
        {
            EndAction();
            return;
        }

        try
        {
            showStatus(status);
            refreshState();
            await submit(request).ConfigureAwait(true);
            if (ownsController()) startMonitor();
        }
        catch (Exception exception)
        {
            if (ownsController()) showError(exception);
        }
        finally
        {
            EndAction();
            if (ownsController()) clearStatus();
        }
    }

    internal async Task<bool> RewindAsync(
        string sessionId,
        int? messageIndex,
        bool historyLoading,
        bool runPending,
        bool rowBelongsToActiveSession,
        Func<bool> ownsSession,
        Func<string, int, Task<bool>> rewind,
        Func<Task<bool>> reload,
        bool actionPending,
        Action showRunPending,
        Action showUnavailable,
        Action showFailure,
        Action<Exception> traceFailure)
    {
        var eligibility = EvaluateRewind(historyLoading, IsRewindInProgress || actionPending, runPending,
            messageIndex is >= 0, rowBelongsToActiveSession);
        if (eligibility == RewindEligibility.RunPending)
        {
            if (ownsSession()) showRunPending();
            return false;
        }
        if (eligibility == RewindEligibility.RowUnavailable)
        {
            if (ownsSession()) showUnavailable();
            return false;
        }
        if (eligibility != RewindEligibility.Allowed || !TryBeginRewind())
        {
            return false;
        }
        try
        {
            if (!ownsSession()) return false;

            bool rewound = await SessionRewindWorkflow.RunAsync(sessionId, messageIndex, ownsSession, rewind, reload)
                .ConfigureAwait(true);
            if (!rewound && ownsSession()) showFailure();
            return rewound;
        }
        catch (Exception exception)
        {
            traceFailure(exception);
            if (ownsSession()) showFailure();
            return false;
        }
        finally
        {
            Interlocked.And(ref _activityState, ~RewindInProgress);
        }
    }

    private bool TryBeginRewind()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activityState);
            if ((current & (ActionPending | RewindInProgress)) != 0) return false;
            if (Interlocked.CompareExchange(ref _activityState, current | RewindInProgress, current) == current) return true;
        }
    }

    internal static AgentApprovalOperation ResolveApproval(bool approved, string? scope)
    {
        if (!approved) return AgentApprovalOperation.Deny;
        return scope switch
        {
            "session" => AgentApprovalOperation.AllowSessionDirectory,
            "always" => AgentApprovalOperation.AllowAlways,
            _ => AgentApprovalOperation.AllowOnce,
        };
    }

    internal static RewindEligibility EvaluateRewind(
        bool historyLoading,
        bool rewindInProgress,
        bool runPending,
        bool hasSavedMessageIndex,
        bool rowBelongsToActiveSession)
    {
        if (historyLoading) return RewindEligibility.HistoryLoading;
        if (rewindInProgress) return RewindEligibility.ActionPending;
        if (runPending) return RewindEligibility.RunPending;
        if (!hasSavedMessageIndex || !rowBelongsToActiveSession) return RewindEligibility.RowUnavailable;
        return RewindEligibility.Allowed;
    }
}
