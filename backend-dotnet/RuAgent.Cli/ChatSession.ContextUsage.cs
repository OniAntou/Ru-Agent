using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>Metadata attempts per run; two is enough for a window to resolve.</summary>
        private const int MaxContextMetadataAttempts = 2;
        private readonly object contextMetadataGate = new();
        private string? contextMetadataRun;
        private int contextMetadataAttempts;

        private async Task ApplyUsageFrameAsync(string runId, SseFrame frame, CancellationToken ct)
        {
            if (frame.IsTransient) return;
            HarnessSafeEventEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data); }
            catch (JsonException) { return; }
            if (envelope is null || envelope.RunId != runId || envelope.SessionId != sessionId) return;

            if (envelope.EventType == "model.step_started")
            {
                usage.BeginStep(envelope.ModelStepId, envelope.Sequence);
                QueuePinnedContextMetadata(runId, ct);
            }
            if (envelope.TotalTokens is not null || envelope.InputTokens is not null || envelope.OutputTokens is not null)
            {
                // A usage report supersedes the prepared request estimate /status context shows.
                if (envelope.EventType == "model.usage_recorded" && preparedContext?.Run == envelope.RunId)
                    preparedContext = null;
                usage.ApplyBilling(envelope.InputTokens, envelope.OutputTokens);
                lastTotalTokens = envelope.TotalTokens ?? ((envelope.InputTokens ?? 0) + (envelope.OutputTokens ?? 0));
                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
            }
            if (tgMode && envelope.EventType == "model.usage_recorded")
            {
                QueuePinnedContextMetadata(runId, ct);
                if (!string.IsNullOrWhiteSpace(envelope.PayloadRef))
                {
                    // The payload costs an HTTP round trip. It must not run on the
                    // frame loop: a busy Host would stall transcript rendering for
                    // the request deadline on every step. ObserveUsage is ordered by
                    // sequence, so a late result still closes its own step (or is
                    // discarded once a newer step has advanced the cursor).
                    QueueUsagePayload(runId, envelope.Sequence, envelope.ModelStepId, envelope.PayloadRef!, ct);
                }
                else
                {
                    // Even a missing usage payload closes this step against a delayed estimate.
                    usage.ObserveUsage(envelope.Sequence, envelope.ModelStepId, null, null);
                }
            }
            if (envelope.EventType == "context.compacted")
            {
                preparedContext = null;
                usage.Compact(envelope.Sequence);
            }
            PaintContextUsage();
        }

        /// <summary>
        /// Starts the bounded metadata read without blocking the frame loop. The
        /// read is advisory: failures stay silent and never fail the turn.
        /// </summary>
        private void QueuePinnedContextMetadata(string runId, CancellationToken ct)
        {
            if (!tgMode) return;
            lock (contextMetadataGate)
            {
                if (contextMetadataRun != runId)
                {
                    contextMetadataRun = runId;
                    contextMetadataAttempts = 0;
                }

                if (contextMetadataAttempts >= MaxContextMetadataAttempts) return;
                contextMetadataAttempts++;
            }

            _ = ReadPinnedContextMetadataAsync(runId, ct);
        }

        private async Task ReadPinnedContextMetadataAsync(string runId, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var run = await _agentClient.GetRunAsync(runId, deadline.Token).ConfigureAwait(false);
                if (run.RunId != runId || run.SessionId != sessionId || !usage.IsBoundTo(runId)) return;
                if (string.IsNullOrWhiteSpace(run.ProviderProtocol)) return;
                usage.ProviderProtocol = run.ProviderProtocol;
                usage.ContextLimit = run.ProviderProtocol is "chat_completions" or "openai_responses" or "anthropic_messages"
                    ? run.ContextWindowTokens : null;
                if (run.ContextWindowTokens is > 0)
                {
                    lock (contextMetadataGate)
                    {
                        if (contextMetadataRun == runId) contextMetadataAttempts = MaxContextMetadataAttempts;
                    }
                }

                PaintContextUsage();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The turn ended; the reading no longer belongs to this view.
            }
            catch (Exception ex)
            {
                TuiDebug.Log("usage", $"pinned context metadata unavailable: {ex.GetType().Name}");
            }
        }

        /// <summary>Starts the usage-payload read without blocking the frame loop.</summary>
        private void QueueUsagePayload(string runId, long sequence, string? stepId, string payloadRef, CancellationToken ct)
        {
            _ = ReadUsagePayloadAsync(runId, sequence, stepId, payloadRef, ct);
        }

        private async Task ReadUsagePayloadAsync(string runId, long sequence, string? stepId, string payloadRef, CancellationToken ct)
        {
            long? prompt = null;
            ContextTokenBreakdown? breakdown = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var payload = await _agentClient.GetPayloadContentAsync(runId, payloadRef, deadline.Token).ConfigureAwait(false);
                if (usage.IsBoundTo(runId)
                    && payload.Kind == "modelusage"
                    && payload.Encoding == "json")
                {
                    ProviderPromptTokens.TryRead(payload.Content, usage.ProviderProtocol, out prompt, out breakdown);
                }

                if (!usage.IsBoundTo(runId)) return;
                usage.ObserveUsage(sequence, stepId, prompt, breakdown);
                PaintContextUsage();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The turn ended; the reading no longer belongs to this view.
            }
            catch (Exception ex)
            {
                TuiDebug.Log("usage", $"request usage unavailable: {ex.GetType().Name}");
            }
        }

        private void ApplyUsageEstimate(SseFrame frame, string runId)
        {
            if (!AgentUsageEstimateModel.TryParse(frame, out var estimate) || estimate is null) return;
            // Kept for /status context until the step's usage report arrives.
            if (preparedContext?.Run != runId || estimate.TransientSequence > preparedContext.Value.Estimate.TransientSequence)
                preparedContext = (runId, estimate);
            if (usage.ObserveEstimate(estimate)) PaintContextUsage();
        }

        private void PaintContextUsage()
        {
            if (!tgMode) return;
            var tokens = usage.ContextTokens ?? 0;
            var percent = usage.ContextPercent;
            var cost = usage.CostKnown ? (decimal?)usage.SessionCost : null;
            var estimated = usage.ContextIsEstimated;
            RunOnTgUi(() => tgWindow!.Header.UpdateUsage(tokens, percent, cost, estimated));
        }
    }
}
