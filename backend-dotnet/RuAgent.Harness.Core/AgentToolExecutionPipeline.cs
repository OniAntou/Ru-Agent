using System.Collections.ObjectModel;
using System.Text.Json;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;

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

    internal AgentToolExecutionPipeline(
        IToolGateway gateway,
        IHarnessEventStore store,
        IIdGenerator ids,
        ITimeProvider time,
        ToolCallScheduler scheduler,
        TransientFrameBuffer frames,
        ITransientContentRedactor transientRedactor)
    {
        _gateway = gateway;
        _store = store;
        _ids = ids;
        _time = time;
        _scheduler = scheduler;
        _frames = frames;
        _transientRedactor = transientRedactor;
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
        var denied = new Dictionary<int, DeniedToolExecution>();
        var orderedReceipts = new List<(int ModelOrder, ToolExecutionReceipt Receipt)>(
            modelCalls.Count);
        for (var index = 0; index < modelCalls.Count; index++)
        {
            var modelCall = modelCalls[index];
            var proposal = new ToolProposal(
                run.RunId,
                modelStepId,
                _ids.NewToolCallId(),
                index + 1,
                modelCall.ToolId,
                modelCall.ToolVersion,
                modelCall.Arguments);
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
            run = await CommitSingleEventAsync(
                    run,
                    run.Status,
                    new HarnessEventIntent(
                        _ids.NewEventId(),
                        HarnessEventKind.ToolProposed,
                        modelStepId: modelStepId,
                        toolCallId: proposal.ToolCallId,
                        payloadId: argumentPayloadId,
                        safeCount: proposal.ModelOrder),
                    new[] { argumentPayload },
                    cancellationToken)
                .ConfigureAwait(false);
            var decision = await _gateway
                .EvaluateAsync(proposal, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("tool_decision_returned_null");
            if (decision.Decision == PolicyDecision.Ask)
            {
                var waiting = AgentBudgetPolicy.EnterApprovalWait(run, _time.UtcNow);
                var approvalEvent = new HarnessEventIntent(
                    _ids.NewEventId(),
                    HarnessEventKind.ApprovalRequested,
                    modelStepId: modelStepId,
                    toolCallId: proposal.ToolCallId,
                    approvalId: decision.ApprovalId,
                    safeCode: decision.SafeCode);
                run = await CommitAsync(
                        run,
                        waiting,
                        new[] { approvalEvent },
                        Array.Empty<HarnessPayloadIntent>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AgentToolPipelineResult(
                    run,
                    Array.Empty<ToolExecutionReceipt>(),
                    "approval_required");
            }

            if (decision.Decision == PolicyDecision.Deny)
            {
                var deniedExecutionId = _ids.NewExecutionId();
                var code = decision.SafeCode ?? "policy_denied";
                var modelContent = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    error = "tool_denied",
                    code,
                });
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
                    deniedExecutionId,
                    proposal.ToolCallId,
                    ToolExecutionOutcome.Failed,
                    code,
                    resultPayloadId,
                    sideEffectsPossible: false,
                    contentEncoding: HarnessPayloadEncoding.Json);
                denied.Add(
                    proposal.ModelOrder,
                    new DeniedToolExecution(resultPayload, receipt));
                continue;
            }

            if (decision.Decision != PolicyDecision.Allow
                || decision.CanonicalIntentToken is null)
            {
                throw new InvalidOperationException("invalid_tool_decision");
            }

            var executionId = _ids.NewExecutionId();
            var approved = new ApprovedToolExecution(
                proposal,
                executionId,
                decision.CanonicalIntentToken);
            var preparedCall = await _gateway
                .PrepareAsync(approved, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("tool_prepare_returned_null");
            ValidatePrepared(approved, decision.AccessKind, preparedCall);
            prepared.Add(preparedCall);
        }

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
                        executionId: call.ExecutionId),
                    preparationPayloads,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var droppedBeforeExecution = _frames.DroppedCount;
        var rawReceipts = await _scheduler.ExecuteAsync(
                prepared,
                (call, token) => ExecuteWithRetryAsync(run.RunId, call, token),
                cancellationToken)
            .ConfigureAwait(false);
        var executionDropped = checked((int)(_frames.DroppedCount - droppedBeforeExecution));
        var rawByOrder = rawReceipts.ToDictionary(
            receipt => prepared.Single(call => call.ExecutionId == receipt.ExecutionId).ModelOrder);
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
                            HarnessEventKind.ToolExecutionCompleted,
                            modelStepId: modelStepId,
                            toolCallId: deniedReceipt.ToolCallId,
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
            if (receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown
                && call.AccessKind != ToolAccessKind.ReadOnly)
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
                    toolCallId: receipt.ToolCallId,
                    executionId: receipt.ExecutionId,
                    safeCode: receipt.Code));
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

    private sealed record DeniedToolExecution(
        HarnessPayloadIntent Payload,
        ToolExecutionReceipt Receipt);

    private async Task<ToolExecutionReceipt> ExecuteWithRetryAsync(
        RunId runId,
        PreparedToolExecution execution,
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
            catch (ToolTransportException) when (
                execution.AccessKind == ToolAccessKind.ReadOnly
                && attempt < maximumAttempts)
            {
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
            || prepared.AccessKind != accessKind)
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
            if (call.ProviderCallId is null)
            {
                writer.WriteNull("provider_call_id");
            }
            else
            {
                writer.WriteString("provider_call_id", call.ProviderCallId);
            }

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
