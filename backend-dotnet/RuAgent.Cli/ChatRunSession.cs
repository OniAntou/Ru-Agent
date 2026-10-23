namespace RuAgent.Cli;

/// <summary>Owns a terminal conversation's turn and retained run identities.</summary>
internal sealed class ChatRunSession : IDisposable
{
    private CancellationTokenSource? _turnCancellation;
    private readonly object _settlementGate = new();
    private TaskCompletionSource? _runSettlement;
    private readonly List<string> _assistantPayloads = new();
    private readonly HashSet<string> _seenPayloads = new(StringComparer.Ordinal);
    private bool _suppressedReply;

    internal string? ActiveRunId { get; private set; }
    internal string? ParkedRunId { get; private set; }
    internal string? FollowableRunId { get; private set; }
    internal bool IsTurnActive => _turnCancellation is not null;
    internal CancellationToken Token => _turnCancellation?.Token
        ?? throw new InvalidOperationException("chat_turn_not_active");
    internal IReadOnlyList<string> AssistantPayloads => _assistantPayloads;

    internal IDisposable RetainRunSettlement()
    {
        lock (_settlementGate)
        {
            if (_runSettlement is not null)
                throw new InvalidOperationException("chat_run_settlement_already_active");
            _runSettlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new RunSettlementLease(this, _runSettlement);
        }
    }

    internal Task WaitForRunSettlementAsync()
    {
        lock (_settlementGate)
            return _runSettlement?.Task ?? Task.CompletedTask;
    }

    private void ReleaseRunSettlement(TaskCompletionSource settlement)
    {
        lock (_settlementGate)
        {
            if (!ReferenceEquals(_runSettlement, settlement)) return;
            _runSettlement = null;
        }
        settlement.TrySetResult();
    }

    private sealed class RunSettlementLease(ChatRunSession owner, TaskCompletionSource settlement) : IDisposable
    {
        private ChatRunSession? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRunSettlement(settlement);
    }

    /// <summary>
    /// True when the turn streamed a reply the host suppressed for recovery. The
    /// streamed text then contains prose that is not part of the durable answer, so the
    /// turn-end gap check cannot tell a lost tail from a superseded reply: it stands down
    /// instead of reporting truncation or reprinting text the user must never see.
    /// </summary>
    internal bool HasSuppressedReply => _suppressedReply;

    internal string? InterruptedRunId => _turnCancellation?.IsCancellationRequested == true
        && ActiveRunId is not null && ActiveRunId != ParkedRunId ? ActiveRunId : null;

    internal void BeginNewTurn(CancellationToken cancellationToken)
    {
        BeginTurn(cancellationToken);
        ActiveRunId = ParkedRunId = FollowableRunId = null;
    }

    internal void BeginContinue(string runId, CancellationToken cancellationToken)
    {
        BeginTurn(cancellationToken);
        ActiveRunId = FollowableRunId = runId;
        // A failed Continue must leave the previous parked identity resumable.
    }

    internal void BeginFollow(string runId, CancellationToken cancellationToken)
    {
        BeginContinue(runId, cancellationToken);
        ParkedRunId = null;
    }

    private void BeginTurn(CancellationToken cancellationToken)
    {
        if (IsTurnActive) throw new InvalidOperationException("chat_turn_already_active");
        _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ClearPayloads();
    }

    internal void AcceptCreatedRun(string runId) => ActiveRunId = FollowableRunId = runId;
    internal void RememberFollowable(string? runId) => FollowableRunId = runId;

    internal void RememberParked(string runId)
    {
        ParkedRunId = runId;
        FollowableRunId = null;
    }

    internal void ParkActiveRun(string runId)
    {
        RememberParked(runId);
        _turnCancellation?.Cancel();
    }

    internal void ForgetParked(string runId)
    {
        if (ParkedRunId == runId) ParkedRunId = null;
    }

    internal void RecordAssistantPayload(string payloadRef)
    {
        if (_seenPayloads.Add(payloadRef)) _assistantPayloads.Add(payloadRef);
    }

    internal void NoteSuppressedReply() => _suppressedReply = true;

    internal void EndTurn()
    {
        _turnCancellation?.Dispose();
        _turnCancellation = null;
    }

    internal void ForgetCompletedRun() => ActiveRunId = FollowableRunId = null;

    internal void ResetSession()
    {
        EndTurn();
        ActiveRunId = ParkedRunId = FollowableRunId = null;
        ClearPayloads();
    }

    private void ClearPayloads()
    {
        _assistantPayloads.Clear();
        _seenPayloads.Clear();
        _suppressedReply = false;
    }

    public void Dispose()
    {
        _turnCancellation?.Cancel();
        ResetSession();
    }
}
