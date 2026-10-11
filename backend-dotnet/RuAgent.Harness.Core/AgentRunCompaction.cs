using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core;

/// <summary>
/// Owns context-window budgeting for a run: the preflight check before each model
/// step plus explicit manual compaction. Moved verbatim from AgentRunCoordinator;
/// per-run gates serialize compaction against preflight so a manual compact can
/// never interleave with an automatic one for the same run.
/// </summary>
internal sealed class AgentRunCompaction
{
    private readonly IHarnessEventStore _store;
    private readonly AgentContextAssembler _contextAssembler;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly IModelContextLimitProvider? _contextLimit;
    private readonly ContextBudgetPolicy _contextBudgetPolicy;
    private readonly IAgentActivitySink? _activitySink;

    /// <summary>
    /// Gate bookkeeping. One entry per run, reference-counted so the entry is
    /// removed only when no caller holds or waits on it. Removing on "semaphore
    /// idle" alone let a waiter that had already captured the old semaphore
    /// overlap with a newcomer that got a fresh one; the overlap surfaced as
    /// duplicate compaction work and a commit conflict. Refcounting closes that
    /// window instead of tolerating it.
    /// </summary>
    private readonly object _gatesLock = new();
    private readonly Dictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);

    private sealed class GateEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int RefCount { get; set; }
    }

    public AgentRunCompaction(
        IHarnessEventStore store,
        AgentContextAssembler contextAssembler,
        IIdGenerator ids,
        ITimeProvider time,
        IModelContextLimitProvider? contextLimit,
        ContextBudgetPolicy contextBudgetPolicy,
        IAgentActivitySink? activitySink = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contextAssembler = contextAssembler
            ?? throw new ArgumentNullException(nameof(contextAssembler));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _contextLimit = contextLimit;
        _contextBudgetPolicy = contextBudgetPolicy
            ?? throw new ArgumentNullException(nameof(contextBudgetPolicy));
        _activitySink = activitySink;
    }

    public async Task<ContextCompactionResult> CompactAsync(
        RunId runId,
        ContextCompactionStrategy strategy,
        ContextCompactionReason reason,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return await UnderRunGateAsync(
                runId.Value,
                async () =>
                {
                    var run = await RequireRunAsync(runId, cancellationToken).ConfigureAwait(false);
                    if (run.State.IsTerminal())
                    {
                        return ContextCompactionResult.NoOp(run, strategy);
                    }

                    long? providerLimit = _contextLimit is null
                        ? null
                        : await _contextLimit.MaxPromptTokensAsync(runId, cancellationToken).ConfigureAwait(false);
                    if (providerLimit is not { } limitTokens)
                    {
                        return ContextCompactionResult.NotAvailable(run, strategy);
                    }

                    var context = await _contextAssembler
                        .AssembleAsync(runId, cancellationToken)
                        .ConfigureAwait(false);
                    return await CompactSnapshotLockedAsync(
                            run,
                            context,
                            limitTokens,
                            strategy,
                            reason,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="body"/> under the run's gate, holding one reference
    /// on the entry for its whole duration: wait, body, release, unref. The
    /// entry (and its semaphore) is removed and disposed only when the last
    /// reference goes away, so a waiter and a newcomer can never end up on two
    /// different semaphores for the same run.
    /// </summary>
    private async Task<T> UnderRunGateAsync<T>(
        string key,
        Func<Task<T>> body,
        CancellationToken cancellationToken)
    {
        GateEntry entry;
        lock (_gatesLock)
        {
            if (!_gates.TryGetValue(key, out entry!))
            {
                entry = new GateEntry();
                _gates[key] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            GateEntry? toDispose = null;
            lock (_gatesLock)
            {
                entry.RefCount--;
                if (entry.RefCount == 0
                    && _gates.TryGetValue(key, out var current)
                    && ReferenceEquals(current, entry))
                {
                    _gates.Remove(key);
                    toDispose = entry;
                }
            }

            toDispose?.Gate.Dispose();
        }
    }

    internal async Task<ContextPreflightResult> PreflightContextAsync(
        AgentRun run,
        ModelContextSnapshot context,
        CancellationToken cancellationToken,
        Func<AgentRun, ModelContextSnapshot, CancellationToken, Task<ModelStepRequest?>>? prepareStep = null)
    {
        long? providerLimit = _contextLimit is null
            ? null
            : await _contextLimit.MaxPromptTokensAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        if (providerLimit is not { } limitTokens)
        {
            return new ContextPreflightResult(run, context, null);
        }

        return await UnderRunGateAsync(
                run.RunId.Value,
                async () =>
                {
                    var preparedRequest = prepareStep is null
                        ? null
                        : await prepareStep(run, context, cancellationToken).ConfigureAwait(false);
                    if (preparedRequest?.Preparation is { } preparation
                        && preparation.MaxPromptTokens != limitTokens)
                        return new ContextPreflightResult(run, context, "provider_metadata_mismatch");
                    var decision = _contextBudgetPolicy.EvaluateAvailablePromptTokens(
                        EstimateInputTokens(context, preparedRequest),
                        limitTokens,
                        ContextCompactionStrategy.Normal);
                    if (decision.NeedsCompaction)
                    {
                        var compaction = await CompactSnapshotLockedAsync(
                                run,
                                context,
                                limitTokens,
                                ContextCompactionStrategy.Normal,
                                ContextCompactionReason.Automatic,
                                cancellationToken,
                                outputReserveAlreadyAccounted: true,
                                estimatedTokensOverride: EstimateInputTokens(context, preparedRequest))
                            .ConfigureAwait(false);
                        if (compaction.Outcome == ContextCompactionOutcome.Committed)
                        {
                            run = compaction.Run!;
                            context = await _contextAssembler
                                .AssembleAsync(run.RunId, cancellationToken)
                                .ConfigureAwait(false);
                            preparedRequest = prepareStep is null
                                ? null
                                : await prepareStep(run, context, cancellationToken).ConfigureAwait(false);
                            if (preparedRequest?.Preparation is { } compactedPreparation
                                && compactedPreparation.MaxPromptTokens != limitTokens)
                                return new ContextPreflightResult(run, context, "provider_metadata_mismatch");
                            decision = _contextBudgetPolicy.EvaluateAvailablePromptTokens(
                                EstimateInputTokens(context, preparedRequest),
                                limitTokens,
                                ContextCompactionStrategy.Normal);
                        }
                        else if (compaction.Outcome is ContextCompactionOutcome.CommitConflict
                            or ContextCompactionOutcome.RunNotFound)
                        {
                            return new ContextPreflightResult(
                                compaction.Run ?? run,
                                null,
                                compaction.ErrorCode ?? "commit_conflict");
                        }
                    }

                    return EstimateInputTokens(context, preparedRequest) > decision.UsablePromptTokens
                        ? new ContextPreflightResult(run, null, "context_limit_reached")
                        : new ContextPreflightResult(run, context, null, preparedRequest);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static long EstimateInputTokens(
        ModelContextSnapshot context,
        ModelStepRequest? preparedRequest) =>
        preparedRequest?.Preparation?.EstimatedInputTokens
            ?? ModelContextSizeEstimator.EstimateTokens(context);

    private async Task<ContextCompactionResult> CompactSnapshotLockedAsync(
        AgentRun run,
        ModelContextSnapshot context,
        long limitTokens,
        ContextCompactionStrategy strategy,
        ContextCompactionReason reason,
        CancellationToken cancellationToken,
        bool outputReserveAlreadyAccounted = false,
        long? estimatedTokensOverride = null)
    {
        var startedAt = _time.UtcNow;
        var estimatedTokens = estimatedTokensOverride ?? ModelContextSizeEstimator.EstimateTokens(context);
        var decision = outputReserveAlreadyAccounted
            ? _contextBudgetPolicy.EvaluateAvailablePromptTokens(estimatedTokens, limitTokens, strategy)
            : _contextBudgetPolicy.Evaluate(estimatedTokens, limitTokens, strategy);
        var eventId = _ids.NewEventId();
        var plan = ContextCompactionPlanner.Plan(
            context,
            decision,
            "compact-" + eventId.Value,
            reason,
            estimatedTokensOverride: estimatedTokensOverride);
        if (plan is null)
        {
            return ContextCompactionResult.NoOp(run, strategy);
        }

        var summaryBytes = Encoding.UTF8.GetBytes(plan.SummaryText);
        var summaryPayload = new HarnessPayloadIntent(
            _ids.NewPayloadId(),
            HarnessPayloadKind.ModelContextSummary,
            HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            Math.Max(summaryBytes.Length, 1),
            summaryBytes);
        var compactedEvent = new HarnessEventIntent(
            eventId,
            HarnessEventKind.ContextCompacted,
            safeCount: checked((int)plan.CoveredEntries.Count),
            safeCode: "context_compacted");
        var next = run.Advance(
            run.Status,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            appendedEventCount: 1,
            updatedAt: _time.UtcNow);
        var commit = await _store.CommitAsync(
                new HarnessCommit(
                    run.RunId,
                    run.Version,
                    next,
                    [compactedEvent],
                    [summaryPayload],
                    Array.Empty<ConversationOutboxIntent>(),
                    new ContextCompactionIntent(
                        run.RunId,
                        plan.CompactionId,
                        summaryPayload.PayloadId,
                        strategy,
                        reason,
                        plan.CoveredRanges,
                        plan.BeforeTokens,
                        plan.AfterTokens,
                        plan.InputHash)),
                cancellationToken)
            .ConfigureAwait(false);
        var result = commit.Outcome switch
        {
            HarnessCommitOutcome.Committed => new ContextCompactionResult(
                ContextCompactionOutcome.Committed,
                commit.Run,
                strategy,
                plan.CompactionId,
                plan.BeforeTokens,
                plan.AfterTokens),
            HarnessCommitOutcome.VersionConflict => new ContextCompactionResult(
                ContextCompactionOutcome.CommitConflict,
                commit.Run,
                strategy,
                ErrorCode: "commit_conflict"),
            HarnessCommitOutcome.RunNotFound => new ContextCompactionResult(
                ContextCompactionOutcome.RunNotFound,
                null,
                strategy,
                ErrorCode: "run_not_found"),
            _ => throw new InvalidOperationException("unknown_commit_outcome"),
        };
        PublishCompactionActivity(
            run.RunId,
            run.LastSequence + 1,
            result.Outcome == ContextCompactionOutcome.Committed,
            plan.CoveredEntries.Count,
            result.ErrorCode,
            startedAt);
        return result;
    }

    /// <summary>
    /// Compaction is a real agent action that has no other client-visible rendering, so
    /// it is projected as a bounded activity. The summary text and covered ranges never
    /// cross the sink boundary.
    /// </summary>
    private void PublishCompactionActivity(
        RunId runId,
        long sequence,
        bool committed,
        int coveredEntries,
        string? errorCode,
        DateTimeOffset startedAt)
    {
        if (_activitySink is null)
        {
            return;
        }

        var elapsed = _time.UtcNow - startedAt;
        var milliseconds = elapsed.TotalMilliseconds;
        AgentActivityProjector.PublishSafe(
            _activitySink,
            runId,
            AgentActivityProjector.ProjectCompaction(
                runId.Value,
                sequence,
                committed ? AgentActivityState.Completed : AgentActivityState.Failed,
                coveredEntries,
                milliseconds is >= 0 and <= 86_400_000 ? (int)milliseconds : null,
                errorCode));
    }

    private async Task<AgentRun> RequireRunAsync(
        RunId runId,
        CancellationToken cancellationToken) =>
        await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("run_not_found");

    internal sealed record ContextPreflightResult(
        AgentRun? Run,
        ModelContextSnapshot? Context,
        string? ErrorCode,
        ModelStepRequest? PreparedRequest = null);
}
