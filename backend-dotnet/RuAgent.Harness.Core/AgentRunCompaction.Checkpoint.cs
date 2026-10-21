using System.Buffers;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunCompaction
{
    private readonly IContextCheckpointSummarizer? _summarizer;
    private readonly ISystemPromptContextProvider? _promptContextProvider;
    private readonly Subagents.IAgentFamilyBudget? _familyBudget;
    private readonly bool _internalRun;

    private async Task<(AgentRun Run, ContextCompactionPlan? Plan, string? ErrorCode)> BuildCheckpointAsync(
        AgentRun run, ContextCompactionPlan plan, CancellationToken ct)
    {
        var limit = _internalRun ? RuAgent.Protocol.Subagents.SubagentLimits.MaxModelSteps : AgentBudgetPolicy.ModelStepLimit;
        if (run.ModelStepsInWindow >= limit)
            return await PauseOrFailCheckpointAsync(run,
                _internalRun ? "subagent_model_step_limit" : "model_step_budget_exhausted", ct).ConfigureAwait(false);
        var stepId = _ids.NewModelStepId();
        if (_familyBudget is not null && !await _familyBudget.ReserveCheckpointAsync(run, stepId, ct).ConfigureAwait(false))
            return await PauseOrFailCheckpointAsync(run, "subagent_family_budget_exhausted", ct).ConfigureAwait(false);

        // Admit auxiliary cost before dispatch. A crash cannot turn a spent model call into a free retry.
        var started = run.Advance(run.Status, run.BudgetWindow, run.ModelStepsInWindow + 1,
            run.ToolCallsInWindow, 1, _time.UtcNow);
        var admitted = await _store.CommitAsync(new HarnessCommit(run.RunId, run.Version, started,
            [new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunNoteAdded,
                modelStepId: stepId, safeCode: "checkpoint_started")], [], []), ct).ConfigureAwait(false);
        if (admitted.Outcome != HarnessCommitOutcome.Committed)
            return (admitted.Run ?? run, null, "commit_conflict");
        run = admitted.Run!;
        try
        {
            var entries = plan.CoveredEntries.ToArray();
            var snapshot = new ModelContextSnapshot(entries, ModelContextSnapshot.Canonicalize(entries));
            var prompt = await _promptContextProvider!.BuildAsync(run, snapshot, ct).ConfigureAwait(false);
            var result = await _summarizer!.SummarizeAsync(new ModelStepRequest(run.RunId, stepId, snapshot, prompt,
                run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256), ct).ConfigureAwait(false);

            // Bill reported usage even when the text later fails validation.
            var payloads = new List<HarnessPayloadIntent>();
            var events = new List<HarnessEventIntent>();
            string? receiptError = null;
            if (result.PromptReceipt is { } receipt)
            {
                try
                {
                    receipt.ValidateAgainst(prompt);
                    var id = _ids.NewPayloadId();
                    var bytes = PromptReceiptPayloadCodec.Serialize(receipt);
                    payloads.Add(new HarnessPayloadIntent(id, HarnessPayloadKind.PromptReceipt, HarnessPayloadAudience.InternalOnly,
                        HarnessPayloadEncoding.Json, PromptReceiptPayloadCodec.MaximumBytes, bytes));
                    events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ModelPromptComposed,
                        modelStepId: stepId, payloadId: id, safeCode: "checkpoint_composed"));
                }
                catch (ArgumentException) { receiptError = "checkpoint_receipt_invalid"; }
            }
            else if (_summarizer is IProviderBackedModelStepRunner) receiptError = "checkpoint_receipt_invalid";
            if (result.Usage is { } usage)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer)) usage.WriteTo(writer);
                var id = _ids.NewPayloadId();
                payloads.Add(new HarnessPayloadIntent(id, HarnessPayloadKind.ModelUsage,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Json, buffer.WrittenCount, buffer.WrittenMemory));
                events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ModelUsageRecorded,
                    modelStepId: stepId, payloadId: id, safeCode: "checkpoint_usage"));
            }
            events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunNoteAdded,
                modelStepId: stepId, safeCode: "checkpoint_returned"));
            var next = run.Advance(run.Status, run.BudgetWindow, run.ModelStepsInWindow,
                run.ToolCallsInWindow, events.Count, _time.UtcNow);
            var recorded = await _store.CommitAsync(new HarnessCommit(run.RunId, run.Version, next, events, payloads, []), ct).ConfigureAwait(false);
            if (recorded.Outcome != HarnessCommitOutcome.Committed) return (recorded.Run ?? run, null, "commit_conflict");
            run = recorded.Run!;
            if (receiptError is not null) throw new ContextCheckpointException(receiptError);
            if (result.ErrorCode is not null) throw new ContextCheckpointException(result.ErrorCode);
            var text = ContextCheckpoint.Validate(result.Text);
            var frame = $"[context_compaction id={plan.CompactionId} untrusted=true]\n{text}\n[/context_compaction]";
            var projected = plan.ProjectedEntries.Select(entry => entry.SourceId == plan.CompactionId
                ? ModelContextEntry.Summary(plan.CompactionId, entry.Sequence, frame, plan.CoveredRanges) : entry).ToArray();
            var after = ModelContextSizeEstimator.EstimateTokens(new ModelContextSnapshot(projected, ModelContextSnapshot.Canonicalize(projected)));
            var coveredTokens = ModelContextSizeEstimator.EstimateTokens(snapshot);
            var summaryTokens = (Encoding.UTF8.GetByteCount(frame) + 3L) / 4;
            if (summaryTokens >= coveredTokens) return (run, null, "checkpoint_not_smaller");
            return (run, plan with { SummaryText = frame, ProjectedEntries = projected, AfterTokens = after }, null);
        }
        catch (ContextCheckpointException error) when (error.Code is "checkpoint_timeout" or "checkpoint_provider_transient")
        { return await PauseOrFailCheckpointAsync(run, error.Code, ct).ConfigureAwait(false); }
        catch (ContextCheckpointException error) { return (run, null, error.Code); }
    }

    private async Task<(AgentRun Run, ContextCompactionPlan? Plan, string? ErrorCode)> PauseOrFailCheckpointAsync(
        AgentRun run, string code, CancellationToken ct)
    {
        if (run.State != HarnessRunState.Running) return (run, null, code);
        // Auxiliary limits and transients obey the same root Continue / child failure policy as model calls.
        var reservation = Subagents.SubagentBudgetPolicy.Reject(run, _internalRun, code, _time.UtcNow);
        var committed = await _store.CommitAsync(new HarnessCommit(run.RunId, run.Version, reservation.NextRun,
            [new HarnessEventIntent(_ids.NewEventId(), _internalRun ? HarnessEventKind.RunFailed : HarnessEventKind.ContinueRequested,
                safeCode: code)], [], []), ct).ConfigureAwait(false);
        return (committed.Run ?? run, null, committed.Outcome == HarnessCommitOutcome.Committed ? code : "commit_conflict");
    }
}
