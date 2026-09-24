using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;

namespace RuAgent.Harness.Core;

internal sealed class AgentToolExecutionPipeline
{
    private readonly IToolGateway _gateway;
    private readonly IHarnessEventStore _store;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly ToolCallScheduler _scheduler;
    private readonly TransientFrameBuffer _frames;
    private readonly ITransientContentRedactor _transientRedactor;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly IAgentActivitySink? _activitySink;
    private readonly IRetainedApprovalStash _retainedStash;

    internal AgentToolExecutionPipeline(
        IToolGateway gateway,
        IHarnessEventStore store,
        IIdGenerator ids,
        ITimeProvider time,
        ToolCallScheduler scheduler,
        TransientFrameBuffer frames,
        ITransientContentRedactor transientRedactor,
        ReconciliationInspectionGate? inspectionGate = null,
        IAgentActivitySink? activitySink = null,
        IRetainedApprovalStash? retainedStash = null)
    {
        _gateway = gateway;
        _store = store;
        _ids = ids;
        _time = time;
        _scheduler = scheduler;
        _frames = frames;
        _transientRedactor = transientRedactor;
        _inspectionGate = inspectionGate;
        _activitySink = activitySink;
        _retainedStash = retainedStash ?? new InMemoryRetainedApprovalStash(time);
    }

    internal async Task<AgentToolPipelineResult> ExecuteAsync(
        AgentRun run,
        ModelStepId modelStepId,
        IReadOnlyList<ModelToolCall> modelCalls,
        int modelDroppedCount,
        CancellationToken cancellationToken)
    {
        if (modelDroppedCount > 0)
        {
            run = await CommitSingleEventAsync(
                    run,
                    run.Status,
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.TransientFramesDropped,
                        modelStepId: modelStepId,
                        safeCount: modelDroppedCount),
                    Array.Empty<HarnessPayloadIntent>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var prepared = new List<PreparedToolExecution>(modelCalls.Count);
        var proposals = new Dictionary<int, ToolProposal>(modelCalls.Count);
        var denied = new Dictionary<int, DeniedToolExecution>();
        var asks = new List<AskDecision>(modelCalls.Count);
        try
        {
            for (var index = 0; index < modelCalls.Count; index++)
            {
                var modelCall = modelCalls[index];
                var proposal = new ToolProposal(
                    run.RunId,
                    modelStepId,
                    _ids.NewToolCallId(),
                    modelCall.ProviderCallId,
                    index + 1,
                    modelCall.ToolId,
                    modelCall.ToolVersion,
                    modelCall.Arguments);
                proposals.Add(proposal.ModelOrder, proposal);
                run = await PersistProposalAsync(
                        run,
                        modelStepId,
                        proposal,
                        modelCall,
                        cancellationToken)
                    .ConfigureAwait(false);
                var decision = await EvaluateProposalAsync(
                        run,
                        proposal,
                        prepared,
                        denied,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (decision.Decision == PolicyDecision.Ask)
                {
                    if (decision.ApprovalId is null)
                    {
                        throw new InvalidOperationException("approval_id_missing");
                    }

                    asks.Add(new AskDecision(proposal, decision));
                    continue;
                }
            }

            await RevokeExtraApprovalsAsync(asks, cancellationToken).ConfigureAwait(false);
            if (asks.Count > 0)
            {
                await DiscardPreparedAsync(prepared).ConfigureAwait(false);
                prepared.Clear();
                return await PauseForApprovalAsync(
                        run,
                        modelStepId,
                        asks[0],
                        proposals,
                        denied,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CompleteExecutionsAsync(
                    run,
                    modelStepId,
                    prepared,
                    proposals,
                    denied,
                    new List<(int ModelOrder, ToolExecutionReceipt Receipt)>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DiscardPreparedAsync(prepared).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AgentRun> PersistProposalAsync(
        AgentRun run,
        ModelStepId modelStepId,
        ToolProposal proposal,
        ModelToolCall modelCall,
        CancellationToken cancellationToken)
    {
        var argumentBytes = SerializeModelToolCall(modelCall);
        var argumentPayloadId = _ids.NewPayloadId();
        var argumentPayload = new HarnessPayloadIntent(
            argumentPayloadId,
            HarnessPayloadKind.ToolArguments,
            HarnessPayloadAudience.AuthenticatedClient
                | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            Math.Max(argumentBytes.Length, 1),
            argumentBytes);
        return await CommitSingleEventAsync(
                run,
                run.Status,
                new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.ToolProposed,
                    modelStepId: modelStepId,
                    toolCallId: proposal.ToolCallId,
                    providerCallId: proposal.ProviderCallId,
                    payloadId: argumentPayloadId,
                    safeCount: proposal.ModelOrder),
                new[] { argumentPayload },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ToolProposalDecision> EvaluateProposalAsync(
        AgentRun run,
        ToolProposal proposal,
        List<PreparedToolExecution> prepared,
        Dictionary<int, DeniedToolExecution> denied,
        CancellationToken cancellationToken)
    {
        AgentActivityProjector.PublishSafe(
            _activitySink,
            run.RunId,
            AgentActivityProjector.ProjectTool(
                proposal,
                AgentActivityState.Started,
                durationMs: null));
        var decision = await _gateway
            .EvaluateAsync(proposal, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("tool_decision_returned_null");
        if (decision.Decision == PolicyDecision.Ask)
        {
            return decision;
        }

        if (decision.Decision == PolicyDecision.Deny)
        {
            var deniedExecution = CreateDeniedExecution(
                proposal,
                _ids.NewExecutionId(),
                decision.SafeCode,
                decision.DenyHint);
            denied.Add(proposal.ModelOrder, deniedExecution);
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectTool(
                    proposal,
                    AgentActivityState.Failed,
                    deniedExecution.Receipt));
            return decision;
        }

        if (_inspectionGate is { } gate
            && gate.IsMutationDenied(run.RunId)
            && decision.AccessKind != ToolAccessKind.ReadOnly)
        {
            var gatedExecution = CreateDeniedExecution(
                proposal,
                _ids.NewExecutionId(),
                "inspection_required");
            denied.Add(proposal.ModelOrder, gatedExecution);
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectTool(
                    proposal,
                    AgentActivityState.Failed,
                    gatedExecution.Receipt));
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectStatus(
                    AgentActivityProjector.ActivityId(proposal.ToolCallId.Value) + "_inspection",
                    AgentActivityState.Waiting,
                    "inspection_required",
                    "Run paused for inspection after an ambiguous outcome. Make one read-only inspection, or send Continue to override and resume mutations."));
            return ToolProposalDecision.Deny(decision.AccessKind, "inspection_required");
        }

        if (decision.Decision != PolicyDecision.Allow
            || decision.CanonicalIntentToken is null)
        {
            throw new InvalidOperationException("invalid_tool_decision");
        }

        var approved = new ApprovedToolExecution(
            proposal,
            _ids.NewExecutionId(),
            decision.CanonicalIntentToken,
            decision.GatewayGeneration);
        var preparedCall = await _gateway
            .PrepareAsync(approved, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("tool_prepare_returned_null");
        prepared.Add(preparedCall);
        ValidatePrepared(approved, decision.AccessKind, preparedCall);
        return decision;
    }

    private async Task RevokeExtraApprovalsAsync(
        IReadOnlyList<AskDecision> asks,
        CancellationToken cancellationToken)
    {
        if (asks.Count <= 1)
        {
            return;
        }

        if (_gateway is not IApprovalCleanupGateway cleanup)
        {
            throw new InvalidOperationException("approval_cleanup_unavailable");
        }

        for (var index = 1; index < asks.Count; index++)
        {
            var approvalId = asks[index].Decision.ApprovalId
                ?? throw new InvalidOperationException("approval_id_missing");
            if (!await cleanup.RevokeApprovalAsync(approvalId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException("approval_cleanup_failed");
            }
        }
    }

    private async Task<AgentToolPipelineResult> PauseForApprovalAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AskDecision ask,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        IReadOnlyDictionary<int, DeniedToolExecution> denied,
        CancellationToken cancellationToken,
        bool preserveWaitingState = false)
    {
        var retained = proposals.Values
            .Where(proposal => !denied.ContainsKey(proposal.ModelOrder))
            .OrderBy(proposal => proposal.ModelOrder)
            .ToArray();
        if (retained.Length == 0)
        {
            throw new InvalidOperationException("retained_approval_empty");
        }

        var approvalId = ask.Decision.ApprovalId
            ?? throw new InvalidOperationException("approval_id_missing");
        _retainedStash.Stash(
            run.RunId,
            new RetainedApprovalStep(retained, approvalId, modelStepId, _time.UtcNow));
        foreach (var proposal in retained)
        {
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectTool(
                    proposal,
                    AgentActivityState.Waiting,
                    safeCode: proposal.ToolCallId == ask.Proposal.ToolCallId
                        ? ask.Decision.SafeCode
                        : null));
        }

        var events = new List<HarnessEventIntent>();
        var payloads = new List<HarnessPayloadIntent>();
        var receipts = new List<ToolExecutionReceipt>();
        foreach (var deniedExecution in denied.OrderBy(item => item.Key).Select(item => item.Value))
        {
            var receipt = deniedExecution.Receipt;
            events.Add(new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ToolExecutionRequested,
                modelStepId: modelStepId,
                toolCallId: receipt.ToolCallId,
                providerCallId: receipt.ProviderCallId,
                executionId: receipt.ExecutionId,
                safeCode: "policy_denied"));
            events.Add(new HarnessEventIntent(
                _ids.NewEventId(),
                HarnessEventKind.ToolExecutionCompleted,
                modelStepId: modelStepId,
                toolCallId: receipt.ToolCallId,
                providerCallId: receipt.ProviderCallId,
                executionId: receipt.ExecutionId,
                payloadId: receipt.ResultPayloadId,
                safeCode: receipt.Code,
                toolOutcome: receipt.Outcome,
                sideEffectsPossible: receipt.SideEffectsPossible));
            payloads.Add(deniedExecution.Payload);
            receipts.Add(receipt);
        }

        events.Add(new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.ApprovalRequested,
            modelStepId: modelStepId,
            toolCallId: ask.Proposal.ToolCallId,
            providerCallId: ask.Proposal.ProviderCallId,
            approvalId: ask.Decision.ApprovalId,
            safeCode: ask.Decision.SafeCode));
        var waiting = preserveWaitingState
            ? run.Status
            : AgentBudgetPolicy.EnterApprovalWait(run, _time.UtcNow).Status;
        var next = run.Advance(
            waiting,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow);
        var committed = await CommitAsync(
                run,
                next,
                events,
                payloads,
                cancellationToken)
            .ConfigureAwait(false);
        return new AgentToolPipelineResult(committed, receipts, "approval_required");
    }

    private async Task<AgentToolPipelineResult> CompleteExecutionsAsync(
        AgentRun run,
        ModelStepId modelStepId,
        List<PreparedToolExecution> prepared,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        IReadOnlyDictionary<int, DeniedToolExecution> denied,
        List<(int ModelOrder, ToolExecutionReceipt Receipt)> orderedReceipts,
        CancellationToken cancellationToken)
    {
        var reconciliationRequired = false;
        // Whether the gate was armed when this batch started. Cleared by the first
        // succeeded read-only call so a second read-only call in the same batch
        // cannot record the inspection twice.
        var gateArmedBeforeThisCall = _inspectionGate?.IsMutationDenied(run.RunId) == true;
        foreach (var call in prepared.OrderBy(call => call.ModelOrder))
        {
            var preparationPayloads = call.PreparationPayloads
                .Select(payload => payload.ToIntent(_ids.NewPayloadId()))
                .ToArray();
            run = await CommitSingleEventAsync(
                    run,
                    run.Status,
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.ToolExecutionRequested,
                        modelStepId: modelStepId,
                        toolCallId: call.ToolCallId,
                        providerCallId: proposals[call.ModelOrder].ProviderCallId,
                        executionId: call.ExecutionId,
                        safeCode: call.AccessKind == ToolAccessKind.ReadOnly
                            ? "read_only"
                            : "side_effects_possible"),
                    preparationPayloads,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var droppedBeforeExecution = _frames.DroppedCount;
        var toolDurations = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var rawReceipts = await _scheduler.ExecuteAsync(
                prepared,
                (call, token) => ExecuteTimedAsync(
                    run.RunId,
                    call,
                    proposals,
                    toolDurations,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        var executionDropped = checked((int)(_frames.DroppedCount - droppedBeforeExecution));
        if (executionDropped > 0)
        {
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectDropped(
                    modelStepId,
                    toolFrames: true,
                    droppedCount: executionDropped));
        }
        var rawByOrder = rawReceipts.ToDictionary(
            receipt => prepared.Single(call => call.ExecutionId == receipt.ExecutionId).ModelOrder);
        // The scheduler intentionally stops after an ambiguous mutation outcome.
        // Every earlier ToolExecutionRequested event still needs a terminal receipt;
        // otherwise skipped calls remain dangling in the durable stream and their
        // prepared authorization state can be replayed. Mark them as not executed and
        // discard their preparation state before recording the reconciliation boundary.
        foreach (var skipped in prepared.Where(call => !rawByOrder.ContainsKey(call.ModelOrder)))
        {
            await _gateway.CancelAsync(skipped.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            rawByOrder[skipped.ModelOrder] = CreateSkippedReceipt(skipped);
        }
        var executionDropEventPending = executionDropped > 0;
        foreach (var modelOrder in rawByOrder.Keys.Concat(denied.Keys).Order())
        {
            if (denied.TryGetValue(modelOrder, out var deniedExecution))
            {
                var deniedReceipt = deniedExecution.Receipt;
                run = await CommitSingleEventAsync(
                        run,
                        run.Status,
                        new HarnessEventIntent(
                            _ids.NewEventId(),
                            HarnessEventKind.ToolExecutionRequested,
                            modelStepId: modelStepId,
                            toolCallId: deniedReceipt.ToolCallId,
                            providerCallId: deniedReceipt.ProviderCallId,
                            executionId: deniedReceipt.ExecutionId,
                            safeCode: "policy_denied"),
                        Array.Empty<HarnessPayloadIntent>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                run = await CommitSingleEventAsync(
                        run,
                        run.Status,
                        new HarnessEventIntent(
                            _ids.NewEventId(),
                            HarnessEventKind.ToolExecutionCompleted,
                            modelStepId: modelStepId,
                            toolCallId: deniedReceipt.ToolCallId,
                            providerCallId: deniedReceipt.ProviderCallId,
                            executionId: deniedReceipt.ExecutionId,
                            payloadId: deniedReceipt.ResultPayloadId,
                            safeCode: deniedReceipt.Code,
                            toolOutcome: deniedReceipt.Outcome,
                            sideEffectsPossible: deniedReceipt.SideEffectsPossible),
                        new[] { deniedExecution.Payload },
                        cancellationToken)
                    .ConfigureAwait(false);
                orderedReceipts.Add((modelOrder, deniedReceipt));
                continue;
            }

            var receipt = rawByOrder[modelOrder];
            var call = prepared.Single(item => item.ModelOrder == modelOrder);
            receipt = receipt.WithProviderCallId(proposals[call.ModelOrder].ProviderCallId);
            var receiptPayloads = new List<HarnessPayloadIntent>();
            if (receipt.ResultPayloadId is not null)
            {
                throw new InvalidOperationException("gateway_result_payload_id_forbidden");
            }

            if (!receipt.ModelContent.IsEmpty)
            {
                var resultPayloadId = _ids.NewPayloadId();
                receiptPayloads.Add(new HarnessPayloadIntent(
                    resultPayloadId,
                    HarnessPayloadKind.ToolOutput,
                    HarnessPayloadAudience.AuthenticatedClient
                        | HarnessPayloadAudience.ModelContext,
                    receipt.ContentEncoding,
                    receipt.ModelContent.Length,
                    receipt.ModelContent));
                receipt = receipt.WithResultPayload(resultPayloadId);
            }

            orderedReceipts.Add((call.ModelOrder, receipt));
            var events = new List<HarnessEventIntent>
            {
                new(
                    _ids.NewEventId(),
                    HarnessEventKind.ToolExecutionCompleted,
                    modelStepId: modelStepId,
                    toolCallId: receipt.ToolCallId,
                    providerCallId: receipt.ProviderCallId,
                    executionId: receipt.ExecutionId,
                    payloadId: receipt.ResultPayloadId,
                    safeCode: receipt.Code,
                    toolOutcome: receipt.Outcome,
                    sideEffectsPossible: receipt.SideEffectsPossible),
            };
            if (executionDropEventPending)
            {
                events.Add(new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.TransientFramesDropped,
                    executionId: receipt.ExecutionId,
                    safeCount: executionDropped));
                executionDropEventPending = false;
            }

            var nextStatus = run.Status;
            var ambiguousSideEffects = receipt.SideEffectsPossible
                && receipt.Outcome is ToolExecutionOutcome.Cancelled or ToolExecutionOutcome.TimedOut;
            if ((receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown
                    && call.AccessKind != ToolAccessKind.ReadOnly)
                || ambiguousSideEffects)
            {
                var transition = AgentRunStateMachine.TryTransition(
                    run.Status,
                    HarnessRunTransition.RequireReconciliation);
                if (!transition.Accepted)
                {
                    throw new InvalidOperationException(transition.ErrorCode);
                }

                nextStatus = transition.Next;
                events.Add(new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.ReconciliationRequired,
                    modelStepId: modelStepId,
                    toolCallId: receipt.ToolCallId,
                    providerCallId: receipt.ProviderCallId,
                    executionId: receipt.ExecutionId,
                    safeCode: receipt.Code));
                // Entering ReconciliationRequired parks the run: the runtime loop stops
                // driving it and the only way out is a user reconciliation command. The
                // transition itself must therefore be visible, or the client sits on a
                // stale "Running" status forever with no explanation and no next step.
                events.Add(new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.RunStateChanged,
                    safeCode: "reconciliation_required"));
                reconciliationRequired = true;
            }
            else if (gateArmedBeforeThisCall
                     && call.AccessKind == ToolAccessKind.ReadOnly
                     && receipt.Outcome == ToolExecutionOutcome.Succeeded)
            {
                // A durable read-only inspection receipt is what clears the
                // UnknownContinue mutation gate. The clearing is recorded in the
                // stream as well: the gate has to be reconstructable after a host
                // restart, and only a durable event can carry that.
                _inspectionGate?.RecordInspection(run.RunId);
                gateArmedBeforeThisCall = false;
                events.Add(new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.ReconciliationResolved,
                    modelStepId: modelStepId,
                    toolCallId: receipt.ToolCallId,
                    providerCallId: receipt.ProviderCallId,
                    executionId: receipt.ExecutionId,
                    safeCode: ReconciliationGateProjection.InspectionRecordedCode));
            }

            if (proposals.TryGetValue(call.ModelOrder, out var proposal))
            {
                var activityState = nextStatus.State == HarnessRunState.ReconciliationRequired
                    ? AgentActivityState.Waiting
                    : receipt.Outcome == ToolExecutionOutcome.Succeeded
                        ? AgentActivityState.Completed
                        : AgentActivityState.Failed;
                AgentActivityProjector.PublishSafe(
                    _activitySink,
                    run.RunId,
                    AgentActivityProjector.ProjectTool(
                        proposal,
                        activityState,
                        receipt,
                        durationMs: toolDurations.TryGetValue(call.ExecutionId.Value, out var elapsedMs)
                            ? elapsedMs
                            : null));

                PublishTodoIfPresent(run.RunId, proposal, receipt);
            }

            if (reconciliationRequired)
            {
                // The tool row above only says the call ended ambiguously; without a
                // reason row the user sees a paused run with no stated cause and no
                // stated way out. Mirror the inspection gate's reason activity so every
                // parked-on-reconciliation state carries its own explanation.
                AgentActivityProjector.PublishSafe(
                    _activitySink,
                    run.RunId,
                    AgentActivityProjector.ProjectStatus(
                        AgentActivityProjector.ActivityId(receipt.ToolCallId.Value) + "_reconciliation",
                        AgentActivityState.Waiting,
                        "reconciliation_required",
                        "Run paused: the last action was interrupted or timed out, so its side effects are unverified. Send Continue to resume with a read-only inspection, or stop the run."));
            }

            var next = run.Advance(
                nextStatus,
                run.BudgetWindow,
                run.ModelStepsInWindow,
                run.ToolCallsInWindow,
                events.Count,
                _time.UtcNow);
            run = await CommitAsync(
                    run,
                    next,
                    events,
                    receiptPayloads,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new AgentToolPipelineResult(
            run,
            orderedReceipts
                .OrderBy(item => item.ModelOrder)
                .Select(item => item.Receipt)
                .ToArray(),
            null);
    }

    /// <summary>
    /// Continues a step parked by an approval wait without the model re-proposing.
    /// Stashed proposals are re-evaluated (the fresh approval token allows them)
    /// and prepared anew: pre-wait preparation tokens go stale across the wait.
    /// </summary>
    public async Task<AgentToolPipelineResult> ResumeRetainedAsync(
        AgentRun run,
        RetainedApprovalStep retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(retained);
        ValidateRetainedApprovalStep(run, retained);
        var proposals = retained.Proposals.ToDictionary(proposal => proposal.ModelOrder);
        var prepared = new List<PreparedToolExecution>(retained.Proposals.Count);
        var denied = new Dictionary<int, DeniedToolExecution>();
        var asks = new List<AskDecision>(retained.Proposals.Count);
        try
        {
            foreach (var proposal in retained.Proposals)
            {
                var decision = await EvaluateProposalAsync(
                        run,
                        proposal,
                        prepared,
                        denied,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (decision.Decision == PolicyDecision.Ask)
                {
                    asks.Add(new AskDecision(proposal, decision));
                }
            }

            await RevokeExtraApprovalsAsync(asks, cancellationToken).ConfigureAwait(false);
            if (asks.Count > 0)
            {
                await DiscardPreparedAsync(prepared).ConfigureAwait(false);
                prepared.Clear();
                return await PauseForApprovalAsync(
                        run,
                        retained.ModelStepId,
                        asks[0],
                        proposals,
                        denied,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CompleteExecutionsAsync(
                    run,
                    retained.ModelStepId,
                    prepared,
                    proposals,
                    denied,
                    new List<(int ModelOrder, ToolExecutionReceipt Receipt)>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DiscardPreparedAsync(prepared).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<AgentToolPipelineResult> RefreshRetainedApprovalAsync(
        AgentRun run,
        RetainedApprovalStep retained,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(retained);
        if (run.State != HarnessRunState.WaitingApproval)
        {
            throw new InvalidOperationException("retained_approval_refresh_state_invalid");
        }

        ValidateRetainedApprovalStep(run, retained);
        var proposals = retained.Proposals.ToDictionary(proposal => proposal.ModelOrder);
        var prepared = new List<PreparedToolExecution>(retained.Proposals.Count);
        var denied = new Dictionary<int, DeniedToolExecution>();
        var asks = new List<AskDecision>(retained.Proposals.Count);
        try
        {
            foreach (var proposal in retained.Proposals)
            {
                var decision = await EvaluateProposalAsync(
                        run,
                        proposal,
                        prepared,
                        denied,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (decision.Decision == PolicyDecision.Ask)
                {
                    asks.Add(new AskDecision(proposal, decision));
                }
            }

            await RevokeExtraApprovalsAsync(asks, cancellationToken).ConfigureAwait(false);
            if (asks.Count == 0)
            {
                throw new InvalidOperationException("retained_approval_gate_missing");
            }

            await DiscardPreparedAsync(prepared).ConfigureAwait(false);
            prepared.Clear();
            return await PauseForApprovalAsync(
                    run,
                    retained.ModelStepId,
                    asks[0],
                    proposals,
                    denied,
                    cancellationToken,
                    preserveWaitingState: true)
                .ConfigureAwait(false);
        }
        catch
        {
            await DiscardPreparedAsync(prepared).ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateRetainedApprovalStep(
        AgentRun run,
        RetainedApprovalStep retained)
    {
        var orders = retained.Proposals
            .Select(proposal => proposal.ModelOrder)
            .ToArray();
        if (retained.Proposals.Count == 0
            || retained.Proposals.Any(proposal =>
                proposal.RunId != run.RunId
                || proposal.ModelStepId != retained.ModelStepId)
            || orders.Distinct().Count() != orders.Length
            || retained.Proposals.Select(proposal => proposal.ProviderCallId)
                .Distinct(StringComparer.Ordinal)
                .Count() != retained.Proposals.Count
            || retained.Proposals.Select(proposal => proposal.ToolCallId)
                .Distinct()
                .Count() != retained.Proposals.Count
            || orders.Zip(orders.Skip(1), (left, right) => right > left).Any(isIncreasing => !isIncreasing))
        {
            throw new InvalidOperationException("retained_approval_invalid");
        }
    }

    /// <summary>
    /// Publishes the agent's task list when a todo write succeeds.
    /// </summary>
    /// <remarks>
    /// The list travels as its own activity rather than being inferred by the view from
    /// the tool result, because the tool-output channel carries display text and this is
    /// structured data. The pipeline is where the result is in hand, which is why the
    /// executor does not publish it: the Broker has no activity sink, and handing it one
    /// would put a presentation concern inside the execution authority.
    ///
    /// The rows are read from the receipt's model content, which is the same bytes the
    /// model receives, so the activity and the model's view of the list cannot disagree.
    /// A payload that is missing should be absent rather than fatal: the plan is a
    /// display aid, and failing the run over it would trade a missing checklist for a
    /// lost turn.
    /// </remarks>
    private void PublishTodoIfPresent(RunId runId, ToolProposal proposal, ToolExecutionReceipt receipt)
    {
        if (_activitySink is null
            || proposal.ToolId is not "todo_write"
            || receipt.Outcome != ToolExecutionOutcome.Succeeded)
        {
            return;
        }

        if (AgentTodoPayload.TryRead(receipt.ModelContent.Span) is not { } rows)
        {
            return;
        }

        AgentActivityProjector.PublishSafe(
            _activitySink,
            runId,
            AgentActivityProjector.ProjectTodos($"act_todo_{Guid.NewGuid():N}", rows));
    }

    private async Task DiscardPreparedAsync(IEnumerable<PreparedToolExecution> prepared)
    {
        foreach (var execution in prepared)
        {
            try
            {
                await _gateway.CancelAsync(execution.ExecutionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Cleanup is best-effort. The gateway remains responsible for
                // failing closed when a stale execution is presented later.
            }
        }
    }

    private static ToolExecutionReceipt CreateSkippedReceipt(PreparedToolExecution execution) =>
        new(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Cancelled,
            "not_executed_after_unknown",
            null,
            sideEffectsPossible: false,
            modelContent: JsonSerializer.SerializeToUtf8Bytes(new
            {
                error = "tool_not_executed",
                code = "not_executed_after_unknown",
            }),
            contentEncoding: HarnessPayloadEncoding.Json);

    private DeniedToolExecution CreateDeniedExecution(
        ToolProposal proposal,
        ExecutionId executionId,
        string code,
        string? hint = null)
    {
        // The repair hint names the argument shape to change; without it a denied
        // call is repeated verbatim until the loop guard ends the run.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["error"] = "tool_denied",
            ["code"] = code,
        };
        if (!string.IsNullOrWhiteSpace(hint))
        {
            fields["hint"] = hint;
        }

        var modelContent = JsonSerializer.SerializeToUtf8Bytes(fields);
        var resultPayloadId = _ids.NewPayloadId();
        var resultPayload = new HarnessPayloadIntent(
            resultPayloadId,
            HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient
                | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json,
            modelContent.Length,
            modelContent);
        var receipt = new ToolExecutionReceipt(
            executionId,
            proposal.ToolCallId,
            ToolExecutionOutcome.Failed,
            code,
            resultPayloadId,
            sideEffectsPossible: false,
            contentEncoding: HarnessPayloadEncoding.Json);
        return new DeniedToolExecution(
            resultPayload,
            receipt.WithProviderCallId(proposal.ProviderCallId));
    }

    private sealed record AskDecision(
        ToolProposal Proposal,
        ToolProposalDecision Decision);

    private sealed record DeniedToolExecution(
        HarnessPayloadIntent Payload,
        ToolExecutionReceipt Receipt);

    /// <summary>
    /// Times one scheduled tool call so the completion activity can report a bounded
    /// duration. Timing is observational only: a clock going backwards, or an elapsed
    /// value outside the wire range, simply leaves the duration unreported.
    /// </summary>
    private async Task<ToolExecutionReceipt> ExecuteTimedAsync(
        RunId runId,
        PreparedToolExecution execution,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        ConcurrentDictionary<string, int> durations,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.UtcNow;
        try
        {
            return await ExecuteWithRetryAsync(
                    runId,
                    execution,
                    proposals,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            var elapsed = _time.UtcNow - startedAt;
            var milliseconds = elapsed.TotalMilliseconds;
            if (milliseconds is >= 0 and <= 86_400_000)
            {
                durations[execution.ExecutionId.Value] = (int)milliseconds;
            }
        }
    }

    private async Task<ToolExecutionReceipt> ExecuteWithRetryAsync(
        RunId runId,
        PreparedToolExecution execution,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var outputStream = new TransientContentStream(
                _frames,
                _transientRedactor,
                runId,
                TransientFrameKind.ToolOutput,
                executionId: execution.ExecutionId);
            try
            {
                var receipt = await _gateway.ExecuteAsync(
                        execution,
                        chunk => PublishToolOutput(
                            execution.ExecutionId,
                            outputStream,
                            chunk),
                        cancellationToken)
                    .ConfigureAwait(false);
                outputStream.Complete();
                return receipt;
            }
            catch (ToolTransportException) when (attempt < maximumAttempts)
            {
                // One retry for transport blips regardless of access kind: the
                // call never reached the tool, so retrying cannot double-apply a
                // mutation. The second failure still reports terminally below.
                if (proposals.TryGetValue(execution.ModelOrder, out var retryProposal))
                {
                    AgentActivityProjector.PublishSafe(
                        _activitySink,
                        runId,
                        AgentActivityProjector.ProjectRetry(retryProposal, attempt + 1));
                }
            }
            catch (ToolTransportException)
            {
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    execution.AccessKind == ToolAccessKind.ReadOnly
                        ? ToolExecutionOutcome.Failed
                        : ToolExecutionOutcome.OutcomeUnknown,
                    "transport_failure",
                    null,
                    sideEffectsPossible: execution.AccessKind != ToolAccessKind.ReadOnly);
            }
        }

        throw new InvalidOperationException("unreachable_tool_retry_state");
    }

    private static ValueTask PublishToolOutput(
        ExecutionId expectedExecutionId,
        TransientContentStream stream,
        ToolOutputChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.ExecutionId != expectedExecutionId)
        {
            throw new InvalidOperationException("tool_output_identity_mismatch");
        }

        stream.Append(chunk.Content);
        return ValueTask.CompletedTask;
    }

    private async Task<AgentRun> CommitSingleEventAsync(
        AgentRun run,
        HarnessRunStatus status,
        HarnessEventIntent eventIntent,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        CancellationToken cancellationToken)
    {
        var next = run.Advance(
            status,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            1,
            _time.UtcNow);
        return await CommitAsync(
                run,
                next,
                new[] { eventIntent },
                payloads,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentRun> CommitAsync(
        AgentRun current,
        AgentRun next,
        IReadOnlyList<HarnessEventIntent> events,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        CancellationToken cancellationToken)
    {
        var result = await _store.CommitAsync(
                new HarnessCommit(
                    current.RunId,
                    current.Version,
                    next,
                    events,
                    payloads,
                    Array.Empty<ConversationOutboxIntent>()),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            HarnessCommitOutcome.Committed => result.Run!,
            HarnessCommitOutcome.VersionConflict =>
                throw new InvalidOperationException("run_version_conflict"),
            HarnessCommitOutcome.RunNotFound =>
                throw new KeyNotFoundException("run_not_found"),
            _ => throw new InvalidOperationException("unknown_commit_outcome"),
        };
    }

    private static void ValidatePrepared(
        ApprovedToolExecution approved,
        ToolAccessKind accessKind,
        PreparedToolExecution prepared)
    {
        if (prepared.ExecutionId != approved.ExecutionId
            || prepared.ToolCallId != approved.Proposal.ToolCallId
            || prepared.ModelOrder != approved.Proposal.ModelOrder
            || prepared.AccessKind != accessKind
            || prepared.GatewayGeneration != approved.GatewayGeneration)
        {
            throw new InvalidOperationException("prepared_tool_identity_mismatch");
        }
    }

    private static byte[] SerializeModelToolCall(ModelToolCall call)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("provider_call_id", call.ProviderCallId);
            writer.WriteString("tool_id", call.ToolId);
            writer.WriteString("tool_version", call.ToolVersion);
            writer.WritePropertyName("arguments");
            call.Arguments.WriteTo(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

internal sealed record AgentToolPipelineResult
{
    internal AgentToolPipelineResult(
        AgentRun run,
        IReadOnlyList<ToolExecutionReceipt> receipts,
        string? errorCode)
    {
        Run = run;
        Receipts = new ReadOnlyCollection<ToolExecutionReceipt>(receipts.ToArray());
        ErrorCode = errorCode;
    }

    internal AgentRun Run { get; }
    internal IReadOnlyList<ToolExecutionReceipt> Receipts { get; }
    internal string? ErrorCode { get; }
}
