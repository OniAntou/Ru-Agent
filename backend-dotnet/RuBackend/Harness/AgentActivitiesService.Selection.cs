using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public sealed partial class AgentActivitiesService
{
    /// <summary>
    /// One page of replayed rows: the newest candidates below the page's cursor, in
    /// chronological order. A candidate is taken whole (a <c>todo_write</c> completion keeps
    /// both its tool row and its checklist), so a page can exceed <paramref name="limit"/> by
    /// one row. <see cref="ReplayPage.OldestSequence"/> is the oldest candidate this page
    /// considered, including one that could not be rebuilt, so the next page's cursor always
    /// moves past it and "load older" never repeats or stalls.
    /// </summary>
    private async Task<ReplayPage> RebuildNewestRowsAsync(
        RunId runId,
        bool terminal,
        IEnumerable<ReplayCandidate> candidates,
        IReadOnlyDictionary<string, ProposedReplayCall> proposed,
        IReadOnlyDictionary<string, DateTimeOffset> requestedAt,
        IReadOnlyDictionary<string, ReasoningReplayCall> reasoning,
        int limit,
        bool pinLatestTodos,
        CancellationToken ct)
    {
        var ordered = candidates.OrderByDescending(item => item.Sequence).ToList();
        var selected = new List<ReplayedActivityRow>(Math.Min(limit, ordered.Count) + 1);
        var reasoningDetails = new Dictionary<string, (string? Text, PayloadId? PayloadId)>(StringComparer.Ordinal);
        ReplayedActivityRow? latestTodos = null;
        long? oldestConsidered = null;
        var olderCount = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = ordered[index];
            var needsActivity = selected.Count < limit;
            if (!needsActivity)
            {
                if (olderCount == 0) olderCount = ordered.Count - index;
                // Past the page only the newest checklist is still sought, and only for
                // the newest page: an older page shows checklists where they happened.
                if (!pinLatestTodos || latestTodos is not null) break;
            }
            else
            {
                oldestConsidered = candidate.Sequence;
            }

            if (candidate.Completed is { } completed)
            {
                // A checklist may predate the page. Past it, inspect only successful
                // proposals and read a result only for todo_write.
                if (!needsActivity && completed.ToolOutcome != ToolExecutionOutcome.Succeeded) continue;
                if (!proposed.TryGetValue(completed.ToolCallId.Value, out var call)) continue;
                var rows = await RebuildCompletedRowsAsync(runId, call, completed, requestedAt, needsActivity, ct)
                    .ConfigureAwait(false);
                if (rows.Todos is { } todos)
                {
                    latestTodos ??= todos;
                    if (needsActivity) selected.Add(todos);
                }
                if (needsActivity && rows.Tool is { } tool) selected.Add(tool);
            }
            else if (needsActivity && candidate.Pending is { } pending)
            {
                var proposal = await RebuildProposalAsync(runId, pending, ct).ConfigureAwait(false);
                if (proposal is null) continue;
                var activity = terminal
                    ? AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Failed, safeCode: "interrupted")
                    : AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
                selected.Add(new(candidate.Sequence, WithEditPreview(activity, proposal)));
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
                selected.Add(new(candidate.Sequence, AgentActivityProjector.ProjectModelStep(thinking.ModelStepId,
                    AgentActivityState.Completed, thinking.DurationMs,
                    detail: detail.Text, reasoningPayloadId: detail.PayloadId)));
            }
            else if (needsActivity && candidate.Steer is { } steerPayloadId)
            {
                // A steer replays where the model received it, as the user's own words (ADR 0056).
                if (await ReadSteerTextAsync(runId, steerPayloadId, ct).ConfigureAwait(false) is { } steerText)
                {
                    selected.Add(new(candidate.Sequence, AgentActivityProjector.ProjectSteer(
                        "steer_" + steerPayloadId.Value["payload_".Length..], steerText)));
                }
            }
            else if (needsActivity && candidate.Compaction is { } compaction)
            {
                selected.Add(new(candidate.Sequence, new AgentActivity(
                    AgentActivityProjector.CompactionActivityId(runId.Value, compaction.Sequence),
                    AgentActivityKind.Compaction, AgentActivityState.Completed,
                    Count: compaction.CoveredCount, SafeCode: "context_compacted")));
            }
        }

        selected.Reverse();
        // The checklist is a snapshot: the newest page leads with the latest one even when it
        // is older than the page. It is added, never swapped for a row, so no row is lost; a
        // client paging back meets it again at its own place and keeps one copy by id.
        if (pinLatestTodos && latestTodos is not null && !selected.Contains(latestTodos))
            selected.Insert(0, latestTodos);
        return new ReplayPage(selected, oldestConsidered, olderCount);
    }

    private sealed record ReplayPage(List<ReplayedActivityRow> Rows, long? OldestSequence, int OlderCount)
    {
        public bool HasMore => OlderCount > 0;
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
