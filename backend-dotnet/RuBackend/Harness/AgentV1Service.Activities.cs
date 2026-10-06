using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using CoreActivityKind = RuAgent.Harness.Core.AgentActivityKind;
using CoreActivityState = RuAgent.Harness.Core.AgentActivityState;
using CoreAgentActivity = RuAgent.Harness.Core.AgentActivity;
using ProtocolActivityKind = RuAgent.Protocol.Harness.AgentActivityKind;
using ProtocolActivityState = RuAgent.Protocol.Harness.AgentActivityState;
using ProtocolActivityWire = RuAgent.Protocol.Harness.AgentActivityWire;
using ProtocolTodoWire = RuAgent.Protocol.Harness.AgentTodoWire;
using RuAgent.Protocol.Harness;
using RuBackend.Conversation;

namespace RuBackend.Harness;

/// <summary>
/// Replays the durable event stream of a finished (or running) run back into the
/// same activity rows the live feed showed. Activity frames are transient by design,
/// so a fresh client that only reads persisted session messages would otherwise see
/// the assistant prose with none of the tool rows that produced it.
/// </summary>
public sealed partial class AgentV1Service
{
    private const int MaxReplayedActivities = 200;

    /// <summary>
    /// How many narration chunks a replayed transcript may carry. Past this the
    /// whole transcript is dropped and the caller falls back to the session text,
    /// so a pathological run degrades to the old grouped layout, never to a
    /// truncated sentence.
    /// </summary>
    private const int MaxTranscriptSegments = 64;

    /// <summary>
    /// Total narration characters a replayed transcript may carry. Same fail-soft
    /// rule as the segment count: over budget means no transcript at all.
    /// </summary>
    private const int MaxTranscriptChars = 131072;

    public async Task<AgentServiceResult> ListRunActivitiesAsync(string runIdRaw, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var proposed = new Dictionary<string, ProposedReplayCall>(StringComparer.Ordinal);
        var requestedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var completed = new List<CompletedReplayCall>();
        var completedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var stepStartedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var thinking = new List<ThinkingReplayRow>();
        var reasoning = new Dictionary<string, ReasoningReplayCall>(StringComparer.Ordinal);
        var stepTexts = new List<StepTextReplayRow>();
        var compactions = new List<CompactionReplayRow>();

        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            var evt = envelope.Event;
            switch (evt.Kind)
            {
                case "tool.proposed"
                    when evt.ToolCallId is { } proposedCallId
                        && evt.ModelStepId is { } proposedStep
                        && evt.ProviderCallId is { } providerCallId
                        && evt.PayloadId is { } argsPayloadId
                        && evt.SafeCount is { } modelOrder:
                    proposed[proposedCallId.Value] = new ProposedReplayCall(
                        proposedCallId,
                        providerCallId,
                        proposedStep,
                        modelOrder,
                        argsPayloadId,
                        evt.Sequence);
                    break;
                case "tool.execution_requested"
                    when evt.ExecutionId is { } requestedExecutionId:
                    requestedAt[requestedExecutionId.Value] = evt.OccurredAt;
                    break;
                case "tool.execution_completed"
                    when evt.ToolCallId is { } completedCallId
                        && evt.ProviderCallId is { } completedProviderCallId
                        && evt.ExecutionId is { } completedExecutionId:
                    completedCallIds.Add(completedCallId.Value);
                    completed.Add(new CompletedReplayCall(
                        completedCallId,
                        completedProviderCallId,
                        completedExecutionId,
                        evt.PayloadId,
                        evt.SafeCode,
                        evt.ToolOutcome,
                        evt.SideEffectsPossible,
                        evt.OccurredAt,
                        evt.Sequence));
                    break;
                case "model.step_started"
                    when evt.ModelStepId is { } startedStep:
                    stepStartedAt[startedStep.Value] = evt.OccurredAt;
                    break;
                case "model.step_completed"
                    when evt.ModelStepId is { } finishedStep:
                    stepStartedAt.TryGetValue(finishedStep.Value, out var startedAt);
                    int? durationMs = startedAt == default
                        ? null
                        : (int?)Math.Max(0, (evt.OccurredAt - startedAt).TotalMilliseconds);
                    thinking.Add(new ThinkingReplayRow(finishedStep, durationMs, evt.Sequence));
                    if (evt.PayloadId is { } stepPayloadId)
                    {
                        stepTexts.Add(new StepTextReplayRow(stepPayloadId, evt.Sequence));
                    }

                    break;
                case "model.reasoning_recorded"
                    when evt.ModelStepId is { } reasoningStep
                        && evt.PayloadId is { } reasoningPayloadId:
                    reasoning[reasoningStep.Value] = new ReasoningReplayCall(
                        reasoningPayloadId,
                        evt.Sequence);
                    break;
                case "continue.requested"
                    when evt.PayloadId is { } blockedPayloadId:
                    stepTexts.Add(new StepTextReplayRow(blockedPayloadId, evt.Sequence));
                    break;
                case "context.compacted"
                    when string.Equals(evt.SafeCode, "context_compacted", StringComparison.Ordinal):
                    compactions.Add(new CompactionReplayRow(evt.Sequence, evt.SafeCount));
                    break;
            }
        }

        var rows = new List<ReplayedActivityRow>();
        foreach (var compaction in compactions)
        {
            rows.Add(new ReplayedActivityRow(
                compaction.Sequence,
                new CoreAgentActivity(
                    RuAgent.Harness.Core.AgentActivityProjector.CompactionActivityId(runId.Value, compaction.Sequence),
                    CoreActivityKind.Compaction,
                    CoreActivityState.Completed,
                    Count: compaction.CoveredCount,
                    SafeCode: "context_compacted")));
        }
        foreach (var call in completed)
        {
            if (!proposed.TryGetValue(call.ToolCallId.Value, out var proposalCall))
            {
                continue;
            }

            var proposal = await RebuildProposalAsync(runId, proposalCall, cancellationToken).ConfigureAwait(false);
            if (proposal is null
                || !string.Equals(
                    proposal.ProviderCallId,
                    call.ProviderCallId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var receipt = await RebuildReceiptAsync(runId, call, cancellationToken).ConfigureAwait(false);
            if (receipt is null)
            {
                continue;
            }

            requestedAt.TryGetValue(call.ExecutionId.Value, out var requested);
            int? durationMs = requested == default
                ? null
                : (int?)Math.Max(0, (call.CompletedAt - requested).TotalMilliseconds);
            var state = receipt.Outcome == ToolExecutionOutcome.Succeeded
                ? CoreActivityState.Completed
                : CoreActivityState.Failed;
            var activity = WithEditPreview(
                AgentActivityProjector.ProjectTool(proposal, state, receipt, durationMs),
                proposal);

            rows.Add(new ReplayedActivityRow(call.Sequence, activity));

            // The task list rides on the same durable completion as the tool row it came
            // from, because that is where it lives: the payload the model received. Emitting
            // it here is what makes the checklist survive leaving a session -- without it
            // the feed rebuilds and the plan is silently absent, which is indistinguishable
            // from a turn that never wrote one.
            if (proposal.ToolId is "todo_write"
                && AgentTodoPayload.TryRead(receipt.ModelContent.Span) is { } todos)
            {
                rows.Add(new ReplayedActivityRow(
                    call.Sequence,
                    AgentActivityProjector.ProjectTodos(
                        $"act_todo_replay_{call.ToolCallId.Value}", todos)));
            }
        }

        // A call that was proposed but never completed is still in flight when the
        // run itself is alive, and settled as interrupted only once the run is
        // terminal. The live feed settles the same way on run end.
        bool runTerminal = run.State.IsTerminal();
        foreach (var entry in proposed.Values)
        {
            if (completedCallIds.Contains(entry.ToolCallId.Value))
            {
                continue;
            }

            var proposal = await RebuildProposalAsync(runId, entry, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
            {
                continue;
            }

            var pending = runTerminal
                ? AgentActivityProjector.ProjectTool(proposal, CoreActivityState.Failed, safeCode: "interrupted")
                : AgentActivityProjector.ProjectTool(proposal, CoreActivityState.Started);
            rows.Add(new ReplayedActivityRow(entry.Sequence, WithEditPreview(pending, proposal)));
        }

        // Reasoning is recorded beside the step completion, so join it back onto
        // the thinking row it belongs to. Only steps with a thinking row pay for
        // a payload read; anything else stays untouched.
        var thinkingSteps = new HashSet<string>(
            thinking.Select(row => row.ModelStepId.Value),
            StringComparer.Ordinal);
        var reasoningDetails = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in reasoning)
        {
            if (!thinkingSteps.Contains(entry.Key))
            {
                continue;
            }

            var detail = await ReadReasoningDetailAsync(runId, entry.Value.PayloadId, cancellationToken).ConfigureAwait(false);
            if (detail is not null)
            {
                reasoningDetails[entry.Key] = detail;
            }
        }

        foreach (var row in thinking)
        {
            reasoningDetails.TryGetValue(row.ModelStepId.Value, out var detail);
            rows.Add(new ReplayedActivityRow(
                row.Sequence,
                AgentActivityProjector.ProjectModelStep(row.ModelStepId, CoreActivityState.Completed, row.DurationMs, detail: detail)));
        }

        var wires = new List<JsonElement>();
        var cappedRows = rows.OrderBy(item => item.Sequence).Take(MaxReplayedActivities).ToList();
        foreach (var row in cappedRows)
        {
            JsonElement wire;
            try
            {
                wire = JsonDocument.Parse(AgentActivityWireCodec.Serialize(MapActivity(row.Activity, row.Sequence))).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            wires.Add(wire);
        }

        var chunks = await ReadTranscriptChunksAsync(runId, stepTexts, cancellationToken).ConfigureAwait(false);
        var transcript = chunks is null
            ? Array.AsReadOnly(Array.Empty<TranscriptEntry>())
            : RunTranscriptPlanner.Merge(
                cappedRows.Select(row => (row.Sequence, row.Activity.ActivityId)).ToList(),
                chunks);
        return new AgentServiceResult(200, new { activities = wires, transcript });
    }

    /// <summary>
    /// The model's own words in event order, so a reopened session can interleave
    /// narration with the tool rows instead of grouping every row above the text.
    /// </summary>
    /// <remarks>
    /// Session messages persist only the final step's text, while every intermediate
    /// narration lives solely in the durable step payloads read here. Anything
    /// unreadable is skipped; over budget (count or characters) drops the whole
    /// transcript and the caller falls back to the session text, so a pathological
    /// run degrades to the old grouped layout, never to a truncated sentence.
    /// </remarks>
    private async Task<IReadOnlyList<TranscriptChunk>?> ReadTranscriptChunksAsync(
        RunId runId,
        IReadOnlyList<StepTextReplayRow> stepTexts,
        CancellationToken cancellationToken)
    {
        if (stepTexts.Count == 0 || stepTexts.Count > MaxTranscriptSegments)
        {
            return null;
        }

        var chunks = new List<TranscriptChunk>(stepTexts.Count);
        int totalChars = 0;
        foreach (var row in stepTexts.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadStepTextAsync(runId, row.PayloadId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            totalChars += text.Length;
            if (totalChars > MaxTranscriptChars)
            {
                return null;
            }

            chunks.Add(new TranscriptChunk(row.Sequence, text));
        }

        return chunks;
    }

    private async Task<string?> ReadStepTextAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent content;
        try
        {
            content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        if (!content.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient))
        {
            return null;
        }

        string? raw;
        if (content.Kind == HarnessPayloadKind.AssistantStep && content.Encoding == HarnessPayloadEncoding.Json)
        {
            AssistantStep step;
            try
            {
                step = AssistantStepJsonCodec.Deserialize(content.Plaintext);
            }
            catch (InvalidDataException)
            {
                return null;
            }

            raw = step.Text;
        }
        else if (content.Kind == HarnessPayloadKind.AssistantText && content.Encoding == HarnessPayloadEncoding.Utf8Text)
        {
            try
            {
                raw = Encoding.UTF8.GetString(content.Plaintext.Span);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = _metadataPolicy is null ? raw.Trim() : _metadataPolicy.StripVoiceMarkers(raw);
        if (string.IsNullOrWhiteSpace(cleaned)
            || string.Equals(cleaned, MetadataPolicy.EmptyResponseText, StringComparison.Ordinal))
        {
            return null;
        }

        return cleaned;
    }

    private async Task<ToolProposal?> RebuildProposalAsync(
        RunId runId,
        ProposedReplayCall call,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent args;
        try
        {
            args = await _payloadStore.ReadForClientAsync(runId, call.ArgsPayloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        if (args.Kind != HarnessPayloadKind.ToolArguments
            || !args.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient)
            || args.Encoding == HarnessPayloadEncoding.Opaque)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(args.Plaintext);
            var root = document.RootElement;
            if (!root.TryGetProperty("tool_id", out var toolIdElement)
                || !root.TryGetProperty("tool_version", out var toolVersionElement)
                || !root.TryGetProperty("arguments", out var argumentsElement)
                || toolIdElement.ValueKind != JsonValueKind.String
                || toolVersionElement.ValueKind != JsonValueKind.String
                || argumentsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ToolProposal(
                runId,
                call.ModelStepId,
                call.ToolCallId,
                call.ProviderCallId,
                call.ModelOrder,
                toolIdElement.GetString() ?? string.Empty,
                toolVersionElement.GetString() ?? string.Empty,
                argumentsElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<ToolExecutionReceipt?> RebuildReceiptAsync(
        RunId runId,
        CompletedReplayCall call,
        CancellationToken cancellationToken)
    {
        if (call.ToolOutcome is null || call.SideEffectsPossible is null)
        {
            return null;
        }

        ReadOnlyMemory<byte> modelContent = ReadOnlyMemory<byte>.Empty;
        HarnessPayloadEncoding encoding = HarnessPayloadEncoding.Json;
        if (call.ResultPayloadId is { } payloadId)
        {
            HarnessPayloadContent result;
            try
            {
                result = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessStoreException)
            {
                return null;
            }

            if (result.Kind != HarnessPayloadKind.ToolOutput
                || !result.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient)
                || result.Encoding == HarnessPayloadEncoding.Opaque)
            {
                return null;
            }

            modelContent = result.Plaintext.ToArray();
            encoding = result.Encoding;
        }

        try
        {
            return new ToolExecutionReceipt(
                call.ExecutionId,
                call.ToolCallId,
                call.ToolOutcome.Value,
                call.SafeCode ?? "succeeded",
                call.ResultPayloadId,
                call.SideEffectsPossible.Value,
                modelContent,
                encoding,
                call.ProviderCallId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The live feed shows the edit preview from the Started frame and keeps it
    /// when the completion lands. A replay only has one row per call, so carry
    /// the same preview over instead of dropping the diff card.
    /// </summary>
    private static AgentActivity WithEditPreview(AgentActivity activity, ToolProposal proposal)
    {
        if (activity.Detail is not null
            || (!string.Equals(proposal.ToolId, "edit", StringComparison.Ordinal)
                && !string.Equals(proposal.ToolId, "write", StringComparison.Ordinal)))
        {
            return activity;
        }

        var startedPreview = AgentActivityProjector.ProjectTool(proposal, CoreActivityState.Started);
        return startedPreview.Detail is null ? activity : activity with { Detail = startedPreview.Detail };
    }

    /// <summary>
    /// Reads a recorded reasoning excerpt for activity replay. Anything that is
    /// not a client-visible UTF-8 reasoning payload is skipped, never surfaced
    /// as a failure: replay must degrade to a duration-only thinking row.
    /// </summary>
    private async Task<string?> ReadReasoningDetailAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent content;
        try
        {
            content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        if (content.Kind != HarnessPayloadKind.ModelReasoning
            || !content.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient)
            || content.Encoding == HarnessPayloadEncoding.Opaque
            || content.Plaintext.IsEmpty)
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(content.Plaintext.Span);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ProtocolActivityWire MapActivity(AgentActivity activity, long durableSequence) =>
        new(
            activity.ActivityId,
            MapActivityKind(activity.Kind),
            MapActivityState(activity.State),
            activity.FileName,
            activity.LineStart,
            activity.LineEnd,
            activity.Symbol,
            activity.Count,
            activity.Added,
            activity.Removed,
            activity.DurationMs,
            activity.SafeCode,
            activity.ToolDisplay,
            activity.Detail,
            activity.Todos?.Select(todo => new ProtocolTodoWire(todo.Content, todo.Status)).ToArray(),
            durableSequence);

    private static ProtocolActivityKind MapActivityKind(CoreActivityKind kind) => kind switch
    {
        CoreActivityKind.Thinking => ProtocolActivityKind.Thinking,
        CoreActivityKind.Explore => ProtocolActivityKind.Explore,
        CoreActivityKind.Search => ProtocolActivityKind.Search,
        CoreActivityKind.Analyze => ProtocolActivityKind.Analyze,
        CoreActivityKind.Edit => ProtocolActivityKind.Edit,
        CoreActivityKind.Terminal => ProtocolActivityKind.Terminal,
        CoreActivityKind.Compaction => ProtocolActivityKind.Compaction,
        CoreActivityKind.Retry => ProtocolActivityKind.Retry,
        CoreActivityKind.Status => ProtocolActivityKind.Status,
        CoreActivityKind.Todo => ProtocolActivityKind.Todo,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ProtocolActivityState MapActivityState(CoreActivityState state) => state switch
    {
        CoreActivityState.Started => ProtocolActivityState.Started,
        CoreActivityState.Completed => ProtocolActivityState.Completed,
        CoreActivityState.Failed => ProtocolActivityState.Failed,
        CoreActivityState.Waiting => ProtocolActivityState.Waiting,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private sealed record StepTextReplayRow(PayloadId PayloadId, long Sequence);

    private sealed record ProposedReplayCall(
        ToolCallId ToolCallId,
        string ProviderCallId,
        ModelStepId ModelStepId,
        int ModelOrder,
        PayloadId ArgsPayloadId,
        long Sequence);

    private sealed record CompletedReplayCall(
        ToolCallId ToolCallId,
        string ProviderCallId,
        ExecutionId ExecutionId,
        PayloadId? ResultPayloadId,
        string? SafeCode,
        ToolExecutionOutcome? ToolOutcome,
        bool? SideEffectsPossible,
        DateTimeOffset CompletedAt,
        long Sequence);

    private sealed record ThinkingReplayRow(ModelStepId ModelStepId, int? DurationMs, long Sequence);

    private sealed record ReasoningReplayCall(PayloadId PayloadId, long Sequence);

    private sealed record ReplayedActivityRow(long Sequence, AgentActivity Activity);
    private sealed record CompactionReplayRow(long Sequence, int? CoveredCount);
}
