using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public sealed partial class AgentActivitiesService
{
    private async Task<List<ReplayedActivityRow>> RebuildNewestRowsAsync(
        RunId runId,
        bool terminal,
        IEnumerable<ReplayCandidate> candidates,
        IReadOnlyDictionary<string, ProposedReplayCall> proposed,
        IReadOnlyDictionary<string, DateTimeOffset> requestedAt,
        IReadOnlyDictionary<string, ReasoningReplayCall> reasoning,
        CancellationToken ct)
    {
        var selected = new List<ReplayedActivityRow>(MaxReplayedActivities);
        var reasoningDetails = new Dictionary<string, (string? Text, PayloadId? PayloadId)>(StringComparer.Ordinal);
        ReplayedActivityRow? latestTodos = null;
        foreach (var candidate in candidates.OrderByDescending(item => item.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            var needsActivity = selected.Count < MaxReplayedActivities;
            if (!needsActivity && latestTodos is not null) break;

            if (candidate.Completed is { } completed)
            {
                // A checklist may predate the visible tail. Once the tail is full,
                // inspect only successful proposals and read a result only for todo_write.
                if (!needsActivity && completed.ToolOutcome != ToolExecutionOutcome.Succeeded) continue;
                if (!proposed.TryGetValue(completed.ToolCallId.Value, out var call)) continue;
                var rows = await RebuildCompletedRowsAsync(runId, call, completed, requestedAt, needsActivity, ct)
                    .ConfigureAwait(false);
                if (rows.Todos is { } todos)
                {
                    latestTodos ??= todos;
                    Keep(todos);
                }
                if (rows.Tool is { } tool) Keep(tool);
            }
            else if (needsActivity && candidate.Pending is { } pending)
            {
                var proposal = await RebuildProposalAsync(runId, pending, ct).ConfigureAwait(false);
                if (proposal is null) continue;
                var activity = terminal
                    ? AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Failed, safeCode: "interrupted")
                    : AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
                Keep(new(candidate.Sequence, WithEditPreview(activity, proposal)));
            }
            else if (needsActivity && candidate.Thinking is { } thinking)
            {
                var step = thinking.ModelStepId.Value;
                if (!reasoningDetails.TryGetValue(step, out var detail))
                {
                    var text = reasoning.TryGetValue(step, out var recorded)
                        ? await ReadReasoningDetailAsync(runId, recorded.PayloadId, ct).ConfigureAwait(false)
                        : null;
                    // A long reasoning replays as an excerpt that names its payload, read whole on demand.
                    detail = (text, text is null ? (PayloadId?)null : recorded!.PayloadId);
                    reasoningDetails[step] = detail;
                }
                Keep(new(candidate.Sequence, AgentActivityProjector.ProjectModelStep(thinking.ModelStepId,
                    AgentActivityState.Completed, thinking.DurationMs,
                    detail: detail.Text, reasoningPayloadId: detail.PayloadId)));
            }
            else if (needsActivity && candidate.Steer is { } steerPayloadId)
            {
                // A steer replays where the model received it, as the user's own words (ADR 0056).
                if (await ReadSteerTextAsync(runId, steerPayloadId, ct).ConfigureAwait(false) is { } steerText)
                {
                    Keep(new(candidate.Sequence, AgentActivityProjector.ProjectSteer(
                        "steer_" + steerPayloadId.Value["payload_".Length..], steerText)));
                }
            }
            else if (needsActivity && candidate.Compaction is { } compaction)
            {
                Keep(new(candidate.Sequence, new AgentActivity(
                    AgentActivityProjector.CompactionActivityId(runId.Value, compaction.Sequence),
                    AgentActivityKind.Compaction, AgentActivityState.Completed,
                    Count: compaction.CoveredCount, SafeCode: "context_compacted")));
            }
        }

        selected.Reverse();
        if (latestTodos is not null && !selected.Contains(latestTodos)) selected[0] = latestTodos;
        return selected;

        void Keep(ReplayedActivityRow row)
        {
            if (selected.Count < MaxReplayedActivities) selected.Add(row);
        }
    }

    private async Task<(ReplayedActivityRow? Tool, ReplayedActivityRow? Todos)> RebuildCompletedRowsAsync(
        RunId runId, ProposedReplayCall call, CompletedReplayCall completed,
        IReadOnlyDictionary<string, DateTimeOffset> requestedAt, bool needsActivity, CancellationToken ct)
    {
        var proposal = await RebuildProposalAsync(runId, call, ct).ConfigureAwait(false);
        if (proposal is null || proposal.ProviderCallId != completed.ProviderCallId
            || !needsActivity && proposal.ToolId != "todo_write") return (null, null);
        var receipt = await RebuildReceiptAsync(runId, completed, ct).ConfigureAwait(false);
        if (receipt is null) return (null, null);

        ReplayedActivityRow? tool = null;
        if (needsActivity)
        {
            requestedAt.TryGetValue(completed.ExecutionId.Value, out var requested);
            int? duration = requested == default ? null
                : (int?)Math.Max(0, (completed.CompletedAt - requested).TotalMilliseconds);
            var state = receipt.Outcome == ToolExecutionOutcome.Succeeded
                ? AgentActivityState.Completed : AgentActivityState.Failed;
            tool = new(completed.Sequence, WithEditPreview(
                AgentActivityProjector.ProjectTool(proposal, state, receipt, duration), proposal));
        }

        var todos = proposal.ToolId == "todo_write" && receipt.Outcome == ToolExecutionOutcome.Succeeded
            && AgentTodoPayload.TryRead(receipt.ModelContent.Span) is { } items
            ? new ReplayedActivityRow(completed.Sequence, AgentActivityProjector.ProjectTodos(
                $"act_todo_replay_{completed.ToolCallId.Value}", items))
            : null;
        return (tool, todos);
    }

    private sealed record ReplayCandidate(long Sequence,
        CompactionReplayRow? Compaction = null, CompletedReplayCall? Completed = null,
        ProposedReplayCall? Pending = null, ThinkingReplayRow? Thinking = null, PayloadId? Steer = null);
}
