using System.Text.Json;
using RuAgent.Protocol.Harness;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentToolExecutionPipeline
{
    private async Task RevokeExtraApprovalsAsync(
        IReadOnlyList<AskDecision> asks,
        CancellationToken cancellationToken)
    {
        if (asks.Count <= 1)
        {
            return;
        }

        await RevokeApprovalsAsync(asks.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private Task RevokeAllApprovalsAsync(
        IReadOnlyList<AskDecision> asks,
        CancellationToken cancellationToken) =>
        asks.Count == 0
            ? Task.CompletedTask
            : RevokeApprovalsAsync(asks, cancellationToken);

    /// <summary>
    /// Revokes every pending approval a failed batch registered, and never
    /// throws: this runs while the caller unwinds an exception, so a cleanup
    /// failure must not replace the original failure. A gateway that cannot
    /// revoke still fails closed when the stale approval or execution is
    /// presented later.
    /// </summary>
    private async Task RevokeApprovalsBestEffortAsync(IReadOnlyList<AskDecision> asks)
    {
        try
        {
            await RevokeAllApprovalsAsync(asks, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort by contract; the original exception propagates.
        }
    }

    private async Task RevokeApprovalsAsync(
        IReadOnlyList<AskDecision> asks,
        CancellationToken cancellationToken)
    {
        if (asks.Count == 0)
        {
            return;
        }

        if (_gateway is not IApprovalCleanupGateway cleanup)
        {
            throw new InvalidOperationException("approval_cleanup_unavailable");
        }

        foreach (var ask in asks)
        {
            var approvalId = ask.Decision.ApprovalId
                ?? throw new InvalidOperationException("approval_id_missing");
            if (!await cleanup.RevokeApprovalAsync(approvalId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException("approval_cleanup_failed");
            }
        }
    }

    /// <summary>
    /// Intercepts an <c>ask_user</c> proposal before any gateway evaluation: the
    /// Broker never receives the call. A malformed proposal is denied locally so
    /// the model gets a specific repair hint instead of an execution attempt.
    /// </summary>
    private void HandleAskUserProposal(
        ToolProposal proposal,
        Dictionary<int, DeniedToolExecution> denied,
        List<InputAsk> inputAsks)
    {
        if (proposal.ToolVersion == AgentToolDefinitionCatalog.CurrentVersion
            && TryReadAskUserArguments(proposal.Arguments, out var question, out var options))
        {
            inputAsks.Add(new InputAsk(proposal, question, options));
            return;
        }

        var invalid = CreateDeniedExecution(
            proposal,
            _ids.NewExecutionId(),
            "invalid_arguments",
            AskUserArgumentHint);
        denied.Add(proposal.ModelOrder, invalid);
        AgentActivityProjector.PublishSafe(
            _activitySink,
            proposal.RunId,
            AgentActivityProjector.ProjectTool(
                proposal,
                AgentActivityState.Failed,
                invalid.Receipt));
    }

    private static bool TryReadAskUserArguments(
        JsonElement arguments,
        out string question,
        out IReadOnlyList<string> options)
    {
        question = string.Empty;
        options = Array.Empty<string>();
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("question", out var questionNode)
            || questionNode.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("question" or "options"))
            {
                return false;
            }
        }

        var candidate = questionNode.GetString();
        if (candidate is null
            || candidate.Length is 0 or > AskUserMaxQuestionLength
            || candidate.Contains('\0'))
        {
            return false;
        }

        var optionList = new List<string>();
        if (arguments.TryGetProperty("options", out var optionsNode)
            && optionsNode.ValueKind != JsonValueKind.Null)
        {
            if (optionsNode.ValueKind != JsonValueKind.Array
                || optionsNode.GetArrayLength() > AskUserMaxOptions)
            {
                return false;
            }

            foreach (var item in optionsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || item.GetString() is not { } option
                    || option.Length > AskUserMaxOptionLength
                    || option.Contains('\0'))
                {
                    return false;
                }

                optionList.Add(option);
            }
        }

        question = candidate;
        options = optionList;
        return true;
    }

    private static byte[] SerializeInputRequest(
        InputId inputId,
        string question,
        IReadOnlyList<string> options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("input_id", inputId.Value);
            writer.WriteString("question", question);
            writer.WriteStartArray("options");
            foreach (var option in options)
            {
                writer.WriteStringValue(option);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
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

    /// <summary>
    /// Parks the run on an <c>ask_user</c> call. The proposal is durable, so the
    /// answer route can complete the asked call from the durable event stream
    /// alone; no in-memory stash is required for correctness.
    /// </summary>
    private async Task<AgentToolPipelineResult> PauseForInputAsync(
        AgentRun run,
        ModelStepId modelStepId,
        InputAsk ask,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        IReadOnlyDictionary<int, DeniedToolExecution> denied,
        CancellationToken cancellationToken)
    {
        var retained = proposals.Values
            .Where(proposal => !denied.ContainsKey(proposal.ModelOrder))
            .OrderBy(proposal => proposal.ModelOrder)
            .ToArray();
        if (retained.Length == 0)
        {
            throw new InvalidOperationException("retained_input_empty");
        }

        foreach (var proposal in retained)
        {
            AgentActivityProjector.PublishSafe(
                _activitySink,
                run.RunId,
                AgentActivityProjector.ProjectTool(
                    proposal,
                    AgentActivityState.Waiting,
                    safeCode: proposal.ToolCallId == ask.Proposal.ToolCallId
                        ? "input_required"
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

        var inputId = _ids.NewInputId();
        var requestPayloadId = _ids.NewPayloadId();
        var requestBytes = SerializeInputRequest(inputId, ask.Question, ask.Options);
        events.Add(new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.InputRequested,
            modelStepId: modelStepId,
            toolCallId: ask.Proposal.ToolCallId,
            providerCallId: ask.Proposal.ProviderCallId,
            executionId: _ids.NewExecutionId(),
            payloadId: requestPayloadId,
            safeCode: "input_required"));
        payloads.Add(new HarnessPayloadIntent(
            requestPayloadId,
            HarnessPayloadKind.UserInputRequest,
            HarnessPayloadAudience.AuthenticatedClient,
            HarnessPayloadEncoding.Json,
            requestBytes.Length,
            requestBytes));

        var waiting = AgentBudgetPolicy.EnterInputWait(run, _time.UtcNow).Status;
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
        return new AgentToolPipelineResult(committed, receipts, "input_required");
    }
}
