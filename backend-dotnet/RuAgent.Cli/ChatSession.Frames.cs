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

    }
}
