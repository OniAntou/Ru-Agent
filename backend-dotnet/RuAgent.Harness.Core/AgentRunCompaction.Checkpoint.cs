using System.Buffers;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunCompaction
{
    private const int MaximumCheckpointCalls = 2;
    private readonly IContextCheckpointSummarizer? _summarizer;
    private readonly ISystemPromptContextProvider? _promptContextProvider;
    private readonly Subagents.IAgentFamilyBudget? _familyBudget;
    private readonly bool _internalRun;

    private async Task<(AgentRun Run, ContextCompactionPlan? Plan, string? ErrorCode, int AdmittedCalls)> BuildCheckpointAsync(
        AgentRun run, ContextCompactionPlan plan, CancellationToken ct, int maximumCalls = MaximumCheckpointCalls)
    {
        var entries = plan.CoveredEntries.ToArray();
        var snapshot = new ModelContextSnapshot(entries, ModelContextSnapshot.Canonicalize(entries),
            verificationEvidence: plan.VerificationEvidence);
        string? lastInvalidCandidate = null;
        var admittedCalls = 0;
        for (var call = 0; call < maximumCalls; call++)
        {
            var stepId = _ids.NewModelStepId();
            var admitted = await AdmitCheckpointCallAsync(run, stepId, ct).ConfigureAwait(false);
            run = admitted.Run;
            if (admitted.ErrorCode is not null)
            {
                if (admitted.ErrorCode == "commit_conflict")
                    return (run, null, admitted.ErrorCode, admittedCalls);
                var paused = await PauseOrFailCheckpointAsync(run, admitted.ErrorCode, ct).ConfigureAwait(false);
                return (paused.Run, paused.Plan, paused.ErrorCode, admittedCalls);
            }

            // Count the admission explicitly: a window reset moves ModelStepsInWindow
            // backwards, so a before/after counter delta is not a call count.
            admittedCalls++;

            try
            {
                var requestContext = call == 0
                    ? snapshot
                    : ContextCheckpoint.WithRepairDirective(snapshot, lastInvalidCandidate);
                var taskPrompt = await _promptContextProvider!.BuildAsync(run, requestContext, ct).ConfigureAwait(false);
                // Keep definitions referenced by native call/result history while
                // omitting unrelated schemas and duplicated outputs. Returned
                // tool proposals are still rejected, never dispatched.
                var referencedTools = entries.SelectMany(e => e.AssistantStep?.ToolCalls ?? [])
                    .Select(c => (c.ToolId, c.ToolVersion)).ToHashSet();
                var prompt = new SystemPromptContext(taskPrompt.SchemaVersion, taskPrompt.AgentProfile, taskPrompt.Preset,
                    taskPrompt.Environment, taskPrompt.InstructionBlocks,
                    taskPrompt.Capabilities.Where(c => referencedTools.Contains((c.Id, c.Version))).ToArray(),
                    new PromptRuntimeContext(taskPrompt.RuntimeContext.References.Where(r => r.Kind == "nested_instructions").ToArray()));
                var result = await _summarizer!.SummarizeAsync(new ModelStepRequest(run.RunId, stepId, requestContext, prompt,
                    run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, sessionId: run.SessionId), ct).ConfigureAwait(false);

                var accountingError = await RecordCheckpointCallAsync(run, stepId, prompt, result, ct).ConfigureAwait(false);
                if (accountingError.ErrorCode is not null)
                    return (accountingError.Run, null, accountingError.ErrorCode, admittedCalls);
                run = accountingError.Run;
                if (result.ErrorCode is not null)
                {
                    if (result.ErrorCode == "checkpoint_invalid" && call + 1 < maximumCalls)
                    {
                        lastInvalidCandidate = result.Text;
                        continue;
                    }
                    if (result.ErrorCode is "checkpoint_timeout" or "checkpoint_provider_transient")
                    {
                        var paused = await PauseOrFailCheckpointAsync(run, result.ErrorCode, ct).ConfigureAwait(false);
                        return (paused.Run, paused.Plan, paused.ErrorCode, admittedCalls);
                    }
                    return (run, null, result.ErrorCode, admittedCalls);
                }

                string text;
                try { text = ContextCheckpoint.Validate(result.Text); }
                catch (ContextCheckpointException error) when (error.Code == "checkpoint_invalid" && call + 1 < maximumCalls)
                {
                    lastInvalidCandidate = result.Text;
                    continue;
                }

                var frame = ContextCompactionFrame.Wrap($"id={plan.CompactionId} untrusted=true", text);
                var projected = plan.ProjectedEntries.Select(entry => entry.SourceId == plan.CompactionId
                    ? ModelContextEntry.Summary(plan.CompactionId, entry.Sequence, frame, plan.CoveredRanges) : entry).ToArray();
                var after = ModelContextSizeEstimator.EstimateTokens(new ModelContextSnapshot(projected,
                    ModelContextSnapshot.Canonicalize(projected), verificationEvidence: plan.VerificationEvidence));
                var coveredTokens = ModelContextSizeEstimator.EstimateTokens(snapshot);
                var summaryTokens = (Encoding.UTF8.GetByteCount(frame) + 3L) / 4;
                if (summaryTokens >= coveredTokens) return (run, null, "checkpoint_not_smaller", admittedCalls);
                return (run, plan with { SummaryText = frame, ProjectedEntries = projected, AfterTokens = after }, null, admittedCalls);
            }
            catch (ContextCheckpointException error) when (error.Code is "checkpoint_timeout" or "checkpoint_provider_transient")
            {
                var paused = await PauseOrFailCheckpointAsync(run, error.Code, ct).ConfigureAwait(false);
                return (paused.Run, paused.Plan, paused.ErrorCode, admittedCalls);
            }
            catch (ContextCheckpointException error) { return (run, null, error.Code, admittedCalls); }
        }
        return (run, null, "checkpoint_invalid", admittedCalls);
    }

    private async Task<(AgentRun Run, string? ErrorCode)> AdmitCheckpointCallAsync(
        AgentRun run, ModelStepId stepId, CancellationToken ct)
    {
        // A checkpoint is a model call: it takes the same root window as any other
        // step, and a root run may open the next window by itself. Internal
        // children keep their own limit and never take the automatic path.
        var admission = _internalRun
            ? Subagents.SubagentBudgetPolicy.ReserveModelStep(run, _time.UtcNow)
            : AgentBudgetPolicy.ReserveModelStep(run, _time.UtcNow, allowAutomaticWindow: true);
        if (!admission.Granted)
            return (run, admission.ErrorCode!);
        if (_familyBudget is not null && !await _familyBudget.ReserveCheckpointAsync(admission.NextRun, stepId, ct).ConfigureAwait(false))
            return (run, "subagent_family_budget_exhausted");

        // The reservation supplied the (possibly new) window counters; this commit
        // records the checkpoint admission beside them before provider dispatch.
        var started = run.Advance(run.Status, admission.NextRun.BudgetWindow,
            admission.NextRun.ModelStepsInWindow, admission.NextRun.ToolCallsInWindow, 1, _time.UtcNow);
        var admitted = await _store.CommitAsync(new HarnessCommit(run.RunId, run.Version, started,
            [new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunNoteAdded,
                modelStepId: stepId, safeCode: "checkpoint_started")], [], []), ct).ConfigureAwait(false);
        return admitted.Outcome == HarnessCommitOutcome.Committed
            ? (admitted.Run!, null)
            : (admitted.Run ?? run, "commit_conflict");
    }

    private async Task<(AgentRun Run, string? ErrorCode)> RecordCheckpointCallAsync(
        AgentRun run, ModelStepId stepId, SystemPromptContext prompt, ContextCheckpointResult result, CancellationToken ct)
    {
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
        if (recorded.Outcome != HarnessCommitOutcome.Committed) return (recorded.Run ?? run, "commit_conflict");
        return (recorded.Run!, receiptError);
    }

    private async Task<(AgentRun Run, ContextCompactionPlan? Plan, string? ErrorCode)> PauseOrFailCheckpointAsync(
        AgentRun run, string code, CancellationToken ct)
    {
        if (run.State != HarnessRunState.Running) return (run, null, code);
        var reservation = Subagents.SubagentBudgetPolicy.Reject(run, _internalRun, code, _time.UtcNow);
        var committed = await _store.CommitAsync(new HarnessCommit(run.RunId, run.Version, reservation.NextRun,
            [new HarnessEventIntent(_ids.NewEventId(), _internalRun ? HarnessEventKind.RunFailed : HarnessEventKind.ContinueRequested,
                safeCode: code)], [], []), ct).ConfigureAwait(false);
        return (committed.Run ?? run, null, committed.Outcome == HarnessCommitOutcome.Committed ? code : "commit_conflict");
    }
}
