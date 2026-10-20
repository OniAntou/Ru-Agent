using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;


namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>Remembers every step's durable assistant payload so a lost delta tail can be recovered once the run finishes.</summary>
        void RecordAssistantStepPayload(SseFrame frame)
        {
            if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"model.step_completed\"", StringComparison.Ordinal))
            {
                // Remember every step's durable assistant payload so a lost
                // delta tail can be recovered once the run finishes.
                try
                {
                    var stepEnvelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                    if (stepEnvelope is { EventType: "model.step_completed" })
                    {
                        // A suppressed reply (`*_advised`) is audit-only and stays
                        // server-side: recording its ref would make the turn-end
                        // recovery chase text the user must never see, and its
                        // streamed prose is not part of the durable answer.
                        if (stepEnvelope.SafeSummary?.EndsWith("_advised", StringComparison.Ordinal) == true)
                        {
                            runSession.NoteSuppressedReply();
                        }
                        else if (!string.IsNullOrWhiteSpace(stepEnvelope.PayloadRef))
                        {
                            runSession.RecordAssistantPayload(stepEnvelope.PayloadRef);
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Non-conforming frame payload; the recovery falls back to
                    // whatever steps were already recorded.
                }
            }
        }

        /// <summary>Applies the token counters a usage envelope carries to the tally and the header.</summary>
        void ApplyUsageFrame(SseFrame frame)
        {
            if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"total_tokens\"", StringComparison.Ordinal))
            {
                try
                {
                    var usageEnvelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                    if (usageEnvelope is not null
                        && (usageEnvelope.TotalTokens is not null
                            || usageEnvelope.InputTokens is not null
                            || usageEnvelope.OutputTokens is not null))
                    {
                        usage.Apply(usageEnvelope.InputTokens, usageEnvelope.OutputTokens);
                        lastTotalTokens = usageEnvelope.TotalTokens
                            ?? ((usageEnvelope.InputTokens ?? 0) + (usageEnvelope.OutputTokens ?? 0));
                        sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                        if (tgMode)
                        {
                            RunOnTgUi(() => tgWindow!.Header.UpdateUsage(
                                lastTotalTokens ?? 0,
                                usage.ContextPercent,
                                usage.CostKnown ? (decimal)usage.SessionCost : null));
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Non-conforming frame payload; token tally keeps its last value.
                }
            }
        }

        /// <summary>Shows the measured request size of the step about to run before its first token arrives.</summary>
        void ApplyUsageEstimate(SseFrame frame)
        {
            if (frame.IsTransient
                && AgentUsageEstimateModel.TryParse(frame, out var usageEstimate)
                && usageEstimate is not null)
            {
                // The step's measured request size arrives before its first token,
                // so the meter shows the context the model is about to read instead
                // of waiting for the usage report at step end.
                usage.ApplyEstimate(usageEstimate.EstimatedInputTokens, usageEstimate.MaxPromptTokens);
                if (tgMode)
                {
                    RunOnTgUi(() => tgWindow!.Header.UpdateUsage(
                        lastTotalTokens ?? 0,
                        usage.ContextPercent,
                        usage.CostKnown ? (decimal)usage.SessionCost : null));
                }
            }
        }

        /// <summary>Follows the root's live subagent hints: announces what changed and keeps the header strip in step.</summary>
        async Task ApplySubagentChangeAsync(SseFrame frame)
        {
            if (frame.IsTransient
                && AgentSubagentChangeModel.TryParse(frame, out var subagentChange)
                && subagentChange is not null)
            {
                // Each hint carries the whole child set: announce what changed and keep the
                // header strip in step. The durable list stays the source for /subagents.
                var transitions = subagentTracker.Apply(subagentChange);
                if (tgMode)
                {
                    var chips = subagentTracker.Chips;
                    RunOnTgUi(() => tgWindow!.Header.SetSubagents(chips));
                }

                if (!cmd.Json)
                {
                    foreach (var transition in transitions)
                    {
                        await WriteDiagnosticLineAsync(humanOut, Subagents.SubagentLiveTracker.TransitionLine(transition))
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>A compaction restarts the context reading; the session cost keeps accumulating.</summary>
        void ApplyCompactionFrame(SseFrame frame)
        {
            if (!frame.IsTransient && frame.Data is not null)
            {
                try
                {
                    var durableEvent = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                    if (durableEvent?.EventType == "context.compacted")
                    {
                        usage.ResetContextReading();
                        if (tgMode)
                        {
                            RunOnTgUi(() => tgWindow!.Header.UpdateUsage(
                                lastTotalTokens ?? 0,
                                null,
                                usage.CostKnown ? (decimal)usage.SessionCost : null));
                        }
                    }
                }
                catch (JsonException)
                {
                    // Nonconforming event payload cannot move the context boundary.
                }
            }
        }
    }
}
