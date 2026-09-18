using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using CoreActivityKind = RuAgent.Harness.Core.AgentActivityKind;
using CoreActivityState = RuAgent.Harness.Core.AgentActivityState;
using ProtocolActivityKind = RuAgent.Protocol.Harness.AgentActivityKind;
using ProtocolActivityState = RuAgent.Protocol.Harness.AgentActivityState;
using ProtocolActivityWire = RuAgent.Protocol.Harness.AgentActivityWire;
using ProtocolTodoWire = RuAgent.Protocol.Harness.AgentTodoWire;
using RuAgent.Protocol.Harness;

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

        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            var evt = envelope.Event;
            switch (evt.Kind)
            {
                case "tool.proposed"
                    when evt.ToolCallId is { } proposedCallId
                        && evt.ModelStepId is { } proposedStep
                        && evt.PayloadId is { } argsPayloadId
                        && evt.SafeCount is { } modelOrder:
                    proposed[proposedCallId.Value] = new ProposedReplayCall(
                        proposedCallId,
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
                        && evt.ExecutionId is { } completedExecutionId:
                    completedCallIds.Add(completedCallId.Value);
                    completed.Add(new CompletedReplayCall(
                        completedCallId,
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
                    break;
            }
        }

        var rows = new List<ReplayedActivityRow>();
        foreach (var call in completed)
        {
            if (!proposed.TryGetValue(call.ToolCallId.Value, out var proposalCall))
            {
                continue;
            }

            var proposal = await RebuildProposalAsync(runId, proposalCall, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
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

        foreach (var row in thinking)
        {
            rows.Add(new ReplayedActivityRow(
                row.Sequence,
                AgentActivityProjector.ProjectModelStep(row.ModelStepId, CoreActivityState.Completed, row.DurationMs)));
        }

        var wires = new List<JsonElement>();
        foreach (var row in rows.OrderBy(item => item.Sequence).Take(MaxReplayedActivities))
        {
            JsonElement wire;
            try
            {
                wire = JsonDocument.Parse(AgentActivityWireCodec.Serialize(MapActivity(row.Activity))).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            wires.Add(wire);
        }

        return new AgentServiceResult(200, new { activities = wires });
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
                encoding);
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

    private static ProtocolActivityWire MapActivity(AgentActivity activity) =>
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
            activity.Todos?.Select(todo => new ProtocolTodoWire(todo.Content, todo.Status)).ToArray());

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

    private sealed record ProposedReplayCall(
        ToolCallId ToolCallId,
        ModelStepId ModelStepId,
        int ModelOrder,
        PayloadId ArgsPayloadId,
        long Sequence);

    private sealed record CompletedReplayCall(
        ToolCallId ToolCallId,
        ExecutionId ExecutionId,
        PayloadId? ResultPayloadId,
        string? SafeCode,
        ToolExecutionOutcome? ToolOutcome,
        bool? SideEffectsPossible,
        DateTimeOffset CompletedAt,
        long Sequence);

    private sealed record ThinkingReplayRow(ModelStepId ModelStepId, int? DurationMs, long Sequence);

    private sealed record ReplayedActivityRow(long Sequence, AgentActivity Activity);
}
