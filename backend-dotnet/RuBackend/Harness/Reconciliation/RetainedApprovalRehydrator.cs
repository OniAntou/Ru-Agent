using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness.Reconciliation;

public sealed class RetainedApprovalRehydrator
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ITimeProvider _time;

    public RetainedApprovalRehydrator(
        IHarnessEventStore store,
        HarnessPayloadStore payloadStore,
        ITimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _time = time ?? new SystemTimeProvider();
    }

    public async Task<RetainedApprovalStep?> RehydrateAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("retained_approval_run_missing");
        var events = await ReadEventsAsync(runId, cancellationToken).ConfigureAwait(false);
        run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("retained_approval_run_missing");
        var proposalEvents = events
            .Where(item => string.Equals(item.Kind, "tool.proposed", StringComparison.Ordinal))
            .ToArray();
        var approvalEvents = events
            .Where(item => string.Equals(item.Kind, "approval.requested", StringComparison.Ordinal))
            .ToArray();
        ValidateApprovalIds(approvalEvents);
        ValidateExecutionEventPopulation(events, proposalEvents);

        if (proposalEvents.Length == 0)
        {
            if (approvalEvents.Length > 0)
            {
                throw Invalid("retained_approval_ask_missing");
            }

            if (run.State == HarnessRunState.WaitingApproval)
            {
                throw Invalid("retained_approval_missing");
            }

            return null;
        }

        var groups = proposalEvents
            .GroupBy(item => RequireModelStepId(item))
            .OrderBy(group => group.Min(item => item.Sequence))
            .ToArray();
        var groupIds = groups.Select(group => group.Key).ToHashSet();
        foreach (var approval in approvalEvents)
        {
            if (!groupIds.Contains(RequireModelStepId(approval)))
            {
                throw Invalid("retained_approval_ask_missing");
            }
        }

        var approvalRecords = ReadApprovalRecords(events, approvalEvents);
        var unresolvedReconciliations = ReadUnresolvedReconciliationIds(events);
        if (run.State == HarnessRunState.WaitingApproval
            && !approvalRecords.Any(item => item.Event.ModelStepId == groups[^1].Key))
        {
            throw Invalid("retained_approval_missing");
        }

        RetainedApprovalStep? latest = null;
        foreach (var group in groups)
        {
            var batchApprovals = approvalRecords
                .Where(item => item.Event.ModelStepId == group.Key)
                .OrderBy(item => item.Event.Sequence)
                .ToArray();
            var retained = await ValidateBatchAsync(
                    runId,
                    events,
                    group.Key,
                    group.OrderBy(item => item.Sequence).ToArray(),
                    batchApprovals,
                    unresolvedReconciliations,
                    cancellationToken)
                .ConfigureAwait(false);
            if (retained is not null)
            {
                latest = retained;
            }
        }

        return latest;
    }

    public Task<RetainedApprovalStep?> TryRehydrateAsync(
        RunId runId,
        CancellationToken cancellationToken = default) =>
        RehydrateAsync(runId, cancellationToken);

    private async Task<RetainedApprovalStep?> ValidateBatchAsync(
        RunId runId,
        IReadOnlyList<HarnessEvent> allEvents,
        ModelStepId modelStepId,
        IReadOnlyList<HarnessEvent> proposalEvents,
        IReadOnlyList<ApprovalRecord> approvals,
        IReadOnlySet<string> unresolvedReconciliations,
        CancellationToken cancellationToken)
    {
        var assistantEvents = allEvents
            .Where(item => string.Equals(item.Kind, "model.step_completed", StringComparison.Ordinal)
                && item.ModelStepId == modelStepId)
            .ToArray();
        if (assistantEvents.Length != 1
            || assistantEvents[0].PayloadId is not { } assistantPayloadId)
        {
            throw Invalid("retained_approval_assistant_event_invalid");
        }

        var assistant = await ReadAssistantStepAsync(
                runId,
                assistantPayloadId,
                cancellationToken)
            .ConfigureAwait(false);
        if (assistantEvents[0].SafeCount != assistant.ToolCalls.Count)
        {
            throw Invalid("retained_approval_safe_count_invalid");
        }

        if (assistant.ToolCalls.Count == 0)
        {
            throw Invalid("retained_approval_assistant_calls_missing");
        }

        var proposals = await ReadProposalsAsync(
                runId,
                assistant,
                proposalEvents,
                assistantEvents[0].Sequence,
                modelStepId,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var approval in approvals)
        {
            if (approval.Event.Sequence <= proposalEvents[^1].Sequence)
            {
                throw Invalid("retained_approval_order_invalid");
            }

            var ask = proposals.SingleOrDefault(item =>
                item.ToolCallId == RequireToolCallId(approval.Event));
            if (ask is null)
            {
                throw Invalid("retained_approval_ask_missing");
            }

            if (!string.Equals(
                    ask.ProviderCallId,
                    RequireProviderCallId(approval.Event),
                    StringComparison.Ordinal))
            {
                throw Invalid("retained_approval_ask_mismatch");
            }
        }

        var execution = ValidateExecution(
            allEvents,
            modelStepId,
            proposals,
            approvals,
            unresolvedReconciliations);
        if (execution.IsComplete)
        {
            return null;
        }

        if (approvals.Count > 0
            && ApprovalState.IsExpired(approvals[^1].Event.OccurredAt, _time.UtcNow))
        {
            throw Invalid("retained_approval_expired");
        }

        if (approvals.Count == 0)
        {
            throw Invalid("retained_approval_missing");
        }

        var latestApproval = approvals[^1];
        var askToolCallId = RequireToolCallId(latestApproval.Event);
        var retained = proposals
            .Where(item => !execution.Completed.Contains(item.ToolCallId))
            .ToArray();
        if (retained.Length == 0 || !retained.Any(item => item.ToolCallId == askToolCallId))
        {
            throw Invalid("retained_approval_set_invalid");
        }

        return new RetainedApprovalStep(
            retained,
            latestApproval.ApprovalId,
            modelStepId,
            latestApproval.Event.OccurredAt);
    }

    private async Task<IReadOnlyList<ToolProposal>> ReadProposalsAsync(
        RunId runId,
        AssistantStep assistant,
        IReadOnlyList<HarnessEvent> proposalEvents,
        long assistantSequence,
        ModelStepId modelStepId,
        CancellationToken cancellationToken)
    {
        if (assistant.FinishReason != AssistantFinishReason.ToolCalls
            || proposalEvents.Count != assistant.ToolCalls.Count
            || proposalEvents[0].Sequence <= assistantSequence)
        {
            throw Invalid("retained_approval_proposal_count_invalid");
        }

        var assistantByOrder = assistant.ToolCalls
            .Select((call, index) => (Order: index + 1, Call: call))
            .ToDictionary(item => item.Order, item => item.Call);
        var proposals = new List<ToolProposal>(proposalEvents.Count);
        var toolCalls = new HashSet<ToolCallId>();
        var providers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < proposalEvents.Count; index++)
        {
            var proposalEvent = proposalEvents[index];
            var expectedOrder = index + 1;
            if (proposalEvent.SafeCount != expectedOrder)
            {
                throw Invalid("retained_approval_safe_count_invalid");
            }

            var toolCallId = RequireToolCallId(proposalEvent);
            var providerCallId = RequireProviderCallId(proposalEvent);
            var payloadId = proposalEvent.PayloadId
                ?? throw Invalid("retained_approval_payload_missing");
            if (!toolCalls.Add(toolCallId) || !providers.Add(providerCallId))
            {
                throw Invalid("retained_approval_order_invalid");
            }

            if (!assistantByOrder.TryGetValue(expectedOrder, out var assistantCall))
            {
                throw Invalid("retained_approval_assistant_order_invalid");
            }

            if (!string.Equals(assistantCall.ProviderCallId, providerCallId, StringComparison.Ordinal))
            {
                throw Invalid("retained_approval_provider_mismatch");
            }

            var argumentContent = await ReadPayloadAsync(
                    runId,
                    payloadId,
                    cancellationToken)
                .ConfigureAwait(false);
            var arguments = ReadArguments(argumentContent, providerCallId, assistantCall);
            proposals.Add(new ToolProposal(
                runId,
                modelStepId,
                toolCallId,
                providerCallId,
                expectedOrder,
                assistantCall.ToolId,
                assistantCall.ToolVersion,
                arguments));
        }

        return proposals;
    }

    private async Task<AssistantStep> ReadAssistantStepAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        var content = await ReadPayloadAsync(runId, payloadId, cancellationToken)
            .ConfigureAwait(false);
        if (content.Kind != HarnessPayloadKind.AssistantStep
            || content.Encoding != HarnessPayloadEncoding.Json
            || !content.Audience.HasFlag(HarnessPayloadAudience.ModelContext))
        {
            throw Invalid("retained_approval_assistant_payload_invalid");
        }

        try
        {
            var assistant = AssistantStepJsonCodec.Deserialize(content.Plaintext);
            if (assistant.FinishReason != AssistantFinishReason.ToolCalls)
            {
                throw Invalid("retained_approval_assistant_finish_invalid");
            }

            return assistant;
        }
        catch (InvalidDataException exception) when (exception.Message.StartsWith(
            "retained_approval_",
            StringComparison.Ordinal))
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_approval_assistant_payload_invalid");
        }
    }

    private async Task<HarnessPayloadContent> ReadPayloadAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _payloadStore
                .ReadForModelAsync(runId, payloadId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_approval_payload_unreadable");
        }
    }

    private static JsonElement ReadArguments(
        HarnessPayloadContent content,
        string providerCallId,
        ModelToolCall assistantCall)
    {
        if (content.Kind != HarnessPayloadKind.ToolArguments
            || content.Encoding != HarnessPayloadEncoding.Json
            || !content.Audience.HasFlag(HarnessPayloadAudience.ModelContext))
        {
            throw Invalid("retained_approval_arguments_payload_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(content.Plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadString(root, "provider_call_id", out var payloadProvider)
                || !string.Equals(payloadProvider, providerCallId, StringComparison.Ordinal)
                || !TryReadString(root, "tool_id", out var payloadToolId)
                || !string.Equals(payloadToolId, assistantCall.ToolId, StringComparison.Ordinal)
                || !TryReadString(root, "tool_version", out var payloadVersion)
                || !string.Equals(payloadVersion, assistantCall.ToolVersion, StringComparison.Ordinal)
                || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object
                || !JsonNode.DeepEquals(
                    JsonNode.Parse(arguments.GetRawText()),
                    JsonNode.Parse(assistantCall.Arguments.GetRawText())))
            {
                throw Invalid("retained_approval_arguments_mismatch");
            }

            return arguments.Clone();
        }
        catch (InvalidDataException exception) when (exception.Message.StartsWith(
            "retained_approval_",
            StringComparison.Ordinal))
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_approval_arguments_invalid");
        }
    }

    private static ExecutionState ValidateExecution(
        IReadOnlyList<HarnessEvent> events,
        ModelStepId modelStepId,
        IReadOnlyList<ToolProposal> proposals,
        IReadOnlyList<ApprovalRecord> approvals,
        IReadOnlySet<string> unresolvedReconciliations)
    {
        var byToolCall = proposals.ToDictionary(item => item.ToolCallId);
        var requests = new Dictionary<string, HarnessEvent>(StringComparer.Ordinal);
        var completed = new HashSet<ToolCallId>();
        var requestToolCalls = new HashSet<ToolCallId>();
        foreach (var item in events.Where(item =>
            string.Equals(item.Kind, "tool.execution_requested", StringComparison.Ordinal)))
        {
            if (item.ModelStepId is null)
            {
                throw Invalid("retained_approval_execution_model_mismatch");
            }

            if (item.ModelStepId != modelStepId)
            {
                if (item.ToolCallId is { } relatedToolCall
                    && byToolCall.ContainsKey(relatedToolCall))
                {
                    throw Invalid("retained_approval_execution_model_mismatch");
                }

                continue;
            }

            var toolCallId = RequireToolCallId(item);
            var providerCallId = RequireProviderCallId(item);
            var executionId = RequireExecutionId(item);
            if (!byToolCall.TryGetValue(toolCallId, out var proposal)
                || !string.Equals(proposal.ProviderCallId, providerCallId, StringComparison.Ordinal)
                || !requests.TryAdd(executionId.Value, item)
                || !requestToolCalls.Add(toolCallId))
            {
                throw Invalid("retained_approval_execution_mismatch");
            }

            if (approvals.Count > 0
                && !string.Equals(item.SafeCode, "policy_denied", StringComparison.Ordinal))
            {
                EnsureResolvedApprovalBeforeExecution(item, approvals);
            }
        }

        foreach (var item in events.Where(item =>
            string.Equals(item.Kind, "tool.execution_completed", StringComparison.Ordinal)))
        {
            if (item.ModelStepId is null)
            {
                throw Invalid("retained_approval_execution_model_mismatch");
            }

            if (item.ModelStepId != modelStepId)
            {
                if (item.ToolCallId is { } relatedToolCall
                    && byToolCall.ContainsKey(relatedToolCall))
                {
                    throw Invalid("retained_approval_execution_model_mismatch");
                }

                continue;
            }

            var toolCallId = RequireToolCallId(item);
            var providerCallId = RequireProviderCallId(item);
            var executionId = RequireExecutionId(item);
            if (item.ToolOutcome == ToolExecutionOutcome.OutcomeUnknown
                && unresolvedReconciliations.Contains(executionId.Value))
            {
                throw Invalid("retained_approval_execution_unknown");
            }

            if (!byToolCall.TryGetValue(toolCallId, out var proposal)
                || !string.Equals(proposal.ProviderCallId, providerCallId, StringComparison.Ordinal)
                || !requests.TryGetValue(executionId.Value, out var request)
                || RequireToolCallId(request) != toolCallId
                || RequireProviderCallId(request) != providerCallId
                || !completed.Add(toolCallId))
            {
                throw Invalid("retained_approval_execution_mismatch");
            }

            if (request.Sequence >= item.Sequence)
            {
                throw Invalid("retained_approval_execution_order");
            }

            if (approvals.Count > 0
                && !string.Equals(request.SafeCode, "policy_denied", StringComparison.Ordinal))
            {
                EnsureResolvedApprovalBeforeExecution(item, approvals);
            }
        }

        if (requests.Count != events.Count(item =>
                string.Equals(item.Kind, "tool.execution_completed", StringComparison.Ordinal)
                && item.ModelStepId == modelStepId)
            && requests.Count > 0)
        {
            throw Invalid("retained_approval_execution_incomplete");
        }

        if (events.Any(item =>
            string.Equals(item.Kind, "reconciliation.required", StringComparison.Ordinal)
            && (item.ModelStepId == modelStepId
                || (item.ToolCallId is { } toolCallId && byToolCall.ContainsKey(toolCallId)))
            && unresolvedReconciliations.Contains(RequireExecutionId(item).Value)))
        {
            throw Invalid("retained_approval_reconciliation_required");
        }

        return new ExecutionState(completed, completed.Count == proposals.Count);
    }

    private static void EnsureResolvedApprovalBeforeExecution(
        HarnessEvent item,
        IReadOnlyList<ApprovalRecord> approvals)
    {
        var approval = approvals
            .Where(candidate => candidate.Event.Sequence < item.Sequence)
            .LastOrDefault();
        if (approval is null
            || approval.ResolvedSequence is null
            || approval.ResolvedSequence >= item.Sequence)
        {
            throw Invalid("retained_approval_execution_before_approval");
        }
    }

    private static HashSet<string> ReadUnresolvedReconciliationIds(
        IReadOnlyList<HarnessEvent> events)
    {
        var pending = new List<PendingReconciliation>();
        var unresolved = new HashSet<string>(
            events
                .Where(item => string.Equals(item.Kind, "tool.execution_completed", StringComparison.Ordinal)
                    && item.ToolOutcome == ToolExecutionOutcome.OutcomeUnknown)
                .Where(item => item.ExecutionId is not null)
                .Select(item => item.ExecutionId!.Value.Value),
            StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (string.Equals(item.Kind, "reconciliation.required", StringComparison.Ordinal)
                && item.ExecutionId is { } executionId)
            {
                pending.Add(new PendingReconciliation(
                    executionId.Value,
                    item.Sequence,
                    item.ModelStepId,
                    item.ToolCallId));
                unresolved.Add(executionId.Value);
                continue;
            }

            if (!string.Equals(item.Kind, "reconciliation.resolved", StringComparison.Ordinal))
            {
                continue;
            }

            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var candidate = pending[index];
                if (candidate.Sequence >= item.Sequence
                    || !MatchesResolution(item, candidate))
                {
                    continue;
                }

                pending.RemoveAt(index);
                unresolved.Remove(candidate.ExecutionId);
                break;
            }
        }

        return unresolved;
    }

    private static bool MatchesResolution(
        HarnessEvent resolution,
        PendingReconciliation pending) =>
        resolution.ExecutionId is { } executionId
        && resolution.ModelStepId is { } modelStepId
        && resolution.ToolCallId is { } toolCallId
        && string.Equals(executionId.Value, pending.ExecutionId, StringComparison.Ordinal)
        && modelStepId == pending.ModelStepId
        && toolCallId == pending.ToolCallId;

    private static IReadOnlyList<ApprovalRecord> ReadApprovalRecords(
        IReadOnlyList<HarnessEvent> events,
        IReadOnlyList<HarnessEvent> approvalEvents)
    {
        var records = approvalEvents
            .Select(item => new ApprovalRecord(
                item,
                item.ApprovalId ?? throw Invalid("retained_approval_id_missing"),
                null))
            .ToList();
        foreach (var resolution in events.Where(item =>
            string.Equals(item.Kind, "run.state_changed", StringComparison.Ordinal)
            && string.Equals(item.SafeCode, "approval_resolved", StringComparison.Ordinal)))
        {
            var index = records
                .Select((candidate, position) => (candidate, position))
                .Where(item => item.candidate.Event.Sequence < resolution.Sequence
                    && item.candidate.ResolvedSequence is null)
                .Select(item => item.position)
                .LastOrDefault(-1);
            if (index < 0)
            {
                throw Invalid("retained_approval_resolution_invalid");
            }

            records[index] = records[index] with { ResolvedSequence = resolution.Sequence };
        }

        return records;
    }

    private static void ValidateExecutionEventPopulation(
        IReadOnlyList<HarnessEvent> events,
        IReadOnlyList<HarnessEvent> proposalEvents)
    {
        var proposalToolCalls = new Dictionary<ToolCallId, ModelStepId>();
        foreach (var proposal in proposalEvents)
        {
            var toolCallId = RequireToolCallId(proposal);
            var modelStepId = RequireModelStepId(proposal);
            if (!proposalToolCalls.TryAdd(toolCallId, modelStepId))
            {
                throw Invalid("retained_approval_execution_duplicate");
            }
        }

        var requests = new Dictionary<string, HarnessEvent>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (string.Equals(item.Kind, "tool.execution_requested", StringComparison.Ordinal))
            {
                var modelStepId = RequireModelStepId(item);
                var toolCallId = RequireToolCallId(item);
                if (!proposalToolCalls.TryGetValue(toolCallId, out var proposalStep)
                    || proposalStep != modelStepId)
                {
                    throw Invalid("retained_approval_execution_model_mismatch");
                }

                var executionId = RequireExecutionId(item);
                if (!requests.TryAdd(executionId.Value, item))
                {
                    throw Invalid("retained_approval_execution_duplicate");
                }
            }
        }

        var completionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (string.Equals(item.Kind, "tool.execution_completed", StringComparison.Ordinal))
            {
                var modelStepId = RequireModelStepId(item);
                var toolCallId = RequireToolCallId(item);
                if (!proposalToolCalls.TryGetValue(toolCallId, out var proposalStep)
                    || proposalStep != modelStepId)
                {
                    throw Invalid("retained_approval_execution_model_mismatch");
                }

                var executionId = RequireExecutionId(item);
                if (!completionIds.Add(executionId.Value))
                {
                    throw Invalid("retained_approval_execution_duplicate");
                }

                if (!requests.TryGetValue(executionId.Value, out var request)
                    || RequireModelStepId(request) != modelStepId
                    || RequireToolCallId(request) != toolCallId)
                {
                    throw Invalid("retained_approval_execution_mismatch");
                }
            }
            else if (string.Equals(item.Kind, "reconciliation.required", StringComparison.Ordinal))
            {
                var executionId = RequireExecutionId(item);
                if (item.ModelStepId is not { } modelStepId
                    || item.ToolCallId is not { } toolCallId
                    || !requests.TryGetValue(executionId.Value, out var request)
                    || RequireModelStepId(request) != modelStepId
                    || RequireToolCallId(request) != toolCallId)
                {
                    throw Invalid("retained_approval_execution_reconciliation_mismatch");
                }
            }
        }

        ValidateReconciliationEventPopulation(events, requests);
    }

    private static void ValidateReconciliationEventPopulation(
        IReadOnlyList<HarnessEvent> events,
        IReadOnlyDictionary<string, HarnessEvent> requests)
    {
        var required = new Dictionary<string, PendingReconciliation>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (string.Equals(item.Kind, "reconciliation.required", StringComparison.Ordinal))
            {
                var requiredExecutionId = RequireExecutionId(item).Value;
                if (item.ModelStepId is not { } requiredModelStepId
                    || item.ToolCallId is not { } requiredToolCallId
                    || !required.TryAdd(
                        requiredExecutionId,
                        new PendingReconciliation(
                            requiredExecutionId,
                            item.Sequence,
                            requiredModelStepId,
                            requiredToolCallId)))
                {
                    throw Invalid("retained_approval_execution_reconciliation_mismatch");
                }

                continue;
            }

            if (!string.Equals(item.Kind, "reconciliation.resolved", StringComparison.Ordinal))
            {
                continue;
            }

            if (item.ExecutionId is null
                && item.ModelStepId is null
                && item.ToolCallId is null)
            {
                continue;
            }

            if (item.ExecutionId is not { } executionId
                || item.ModelStepId is not { } modelStepId
                || item.ToolCallId is not { } toolCallId
                || !requests.TryGetValue(executionId.Value, out var request)
                || request.Sequence >= item.Sequence
                || request.ModelStepId != modelStepId
                || request.ToolCallId != toolCallId
                || !resolved.Add(executionId.Value))
            {
                throw Invalid("retained_approval_reconciliation_resolution_mismatch");
            }

            if (required.TryGetValue(executionId.Value, out var pending)
                && (pending.Sequence >= item.Sequence
                    || pending.ModelStepId != modelStepId
                    || pending.ToolCallId != toolCallId))
            {
                throw Invalid("retained_approval_reconciliation_resolution_mismatch");
            }
        }
    }

    private static void ValidateApprovalIds(IReadOnlyList<HarnessEvent> approvalEvents)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var approval in approvalEvents)
        {
            var id = approval.ApprovalId
                ?? throw Invalid("retained_approval_id_missing");
            _ = RequireModelStepId(approval);
            _ = RequireToolCallId(approval);
            _ = RequireProviderCallId(approval);
            if (!ids.Add(id.Value))
            {
                throw Invalid("retained_approval_duplicate");
            }
        }
    }

    private async Task<IReadOnlyList<HarnessEvent>> ReadEventsAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var events = new List<HarnessEvent>();
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var previousSequence = 0L;
        await foreach (var envelope in _store
            .ReadEventsAsync(runId, 0, cancellationToken)
            .ConfigureAwait(false))
        {
            if (envelope.Event.RunId != runId
                || envelope.Event.Sequence <= previousSequence
                || !eventIds.Add(envelope.Event.EventId.Value))
            {
                throw Invalid("retained_approval_event_order_invalid");
            }

            previousSequence = envelope.Event.Sequence;
            events.Add(envelope.Event);
        }

        return events;
    }

    private static ModelStepId RequireModelStepId(HarnessEvent item) =>
        item.ModelStepId ?? throw Invalid("retained_approval_model_step_missing");

    private static ToolCallId RequireToolCallId(HarnessEvent item) =>
        item.ToolCallId ?? throw Invalid("retained_approval_tool_call_missing");

    private static ExecutionId RequireExecutionId(HarnessEvent item) =>
        item.ExecutionId ?? throw Invalid("retained_approval_execution_id_missing");

    private static string RequireProviderCallId(HarnessEvent item)
    {
        var value = item.ProviderCallId;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw Invalid("retained_approval_provider_missing");
        }

        return value;
    }

    private static bool TryReadString(
        JsonElement root,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var node)
            || node.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed record PendingReconciliation(
        string ExecutionId,
        long Sequence,
        ModelStepId? ModelStepId,
        ToolCallId? ToolCallId);

    private sealed record ApprovalRecord(
        HarnessEvent Event,
        ApprovalId ApprovalId,
        long? ResolvedSequence);

    private sealed record ExecutionState(
        HashSet<ToolCallId> Completed,
        bool IsComplete);

    private static InvalidDataException Invalid(string code) => new(code);
}
