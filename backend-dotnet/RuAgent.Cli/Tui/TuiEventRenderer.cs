using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed class TuiSessionRenderer
{
    private readonly TextWriter _writer;
    private bool _hasStreamedDelta;
    private bool _isThinking;

    public TuiSessionRenderer(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Reset()
    {
        _hasStreamedDelta = false;
        _isThinking = false;
    }

    public void RenderFrame(SseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.IsTransient)
        {
            RenderTransient(frame);
            return;
        }

        if (frame.DurableSequence is null)
            return;

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch
        {
            return;
        }

        if (envelope is null) return;
        RenderEnvelope(envelope);
    }

    private void RenderTransient(SseFrame frame)
    {
        // 1. Assistant text streaming (stream tokens directly like real chat)
        if (AgentAssistantDeltaModel.TryParse(frame, out var delta) && delta is not null)
        {
            if (!_hasStreamedDelta)
            {
                if (_isThinking)
                {
                    _isThinking = false;
                }
                _writer.WriteLine();
                _writer.Write(AnsiTheme.Bold(AnsiTheme.Cyan("✦ RuAgent: ")));
                _hasStreamedDelta = true;
            }

            _writer.Write(delta.Text);
            _writer.Flush();
            return;
        }

        // 2. Activity status (thinking indicator)
        if (AgentActivityModel.TryParse(frame, out var activity) && activity is not null)
        {
            if (activity.Kind == AgentActivityKind.Thinking)
            {
                if (activity.State == AgentActivityState.Started && !_isThinking && !_hasStreamedDelta)
                {
                    _isThinking = true;
                    _writer.WriteLine(AnsiTheme.Dim("  • Thinking..."));
                    _writer.Flush();
                }
                else if (activity.State == AgentActivityState.Completed)
                {
                    _isThinking = false;
                }
                else if (activity.State == AgentActivityState.Failed)
                {
                    _isThinking = false;
                    _writer.WriteLine(AnsiTheme.Red("  • Model thinking failed."));
                    _writer.Flush();
                }
            }
        }
    }

    public void RenderEnvelope(HarnessSafeEventEnvelope envelope)
    {
        var seq = envelope.Sequence;
        var type = envelope.EventType;
        var status = envelope.Status;
        var summary = Escape(envelope.SafeSummary);

        // Filter out noisy state machine transitions
        if (type is "run.state_changed" or "model.step_started" or "model.step_completed" or "prompt_receipt")
            return;

        if (_hasStreamedDelta)
        {
            _writer.WriteLine();
            _hasStreamedDelta = false;
        }

        switch (type)
        {
            case "model.turn_started":
                _writer.WriteLine(AnsiTheme.Cyan($"┌─ Turn #{seq}: Model Thinking..."));
                break;

            case "tool.execution_requested":
                _writer.WriteLine();
                _writer.WriteLine(AnsiTheme.Yellow($"⚙ [Tool Request #{seq}] ") + AnsiTheme.Bold(summary));
                break;

            case "tool.executed":
                if (status == "succeeded")
                {
                    _writer.WriteLine(AnsiTheme.Green($"✓ [Tool Done #{seq}] ") + summary);
                }
                else
                {
                    _writer.WriteLine(AnsiTheme.Red($"✗ [Tool Failed #{seq}] ") + summary + $" ({status})");
                }
                break;

            case "approval.requested":
                _writer.WriteLine();
                _writer.WriteLine(AnsiTheme.Yellow("╔══════════════════════════ ⚠️  APPROVAL REQUIRED ══════════════════════════╗"));
                _writer.WriteLine(AnsiTheme.Yellow($"║  {summary.PadRight(70)}║"));
                _writer.WriteLine(AnsiTheme.Yellow($"║  Run ID: {envelope.RunId.PadRight(65)}║"));
                _writer.WriteLine(AnsiTheme.Yellow($"║  Approval ID: {(envelope.ApprovalId ?? "").PadRight(60)}║"));
                _writer.WriteLine(AnsiTheme.Yellow("║  Commands:                                                               ║"));
                _writer.WriteLine(AnsiTheme.Yellow("║    agent approve <run-id> <approval-id>                                 ║"));
                _writer.WriteLine(AnsiTheme.Yellow("║    agent deny <run-id> <approval-id>                                     ║"));
                _writer.WriteLine(AnsiTheme.Yellow("╚═══════════════════════════════════════════════════════════════════════════╝"));
                break;

            case "reconciliation.required":
            case "reconciliation.resolved":
                _writer.WriteLine(AnsiTheme.Magenta($"  ✦ Reconciliation: ") + summary);
                break;

            case "transient.frames_dropped":
                _writer.WriteLine(AnsiTheme.Red($"  ⚠ Dropped frames: ") + summary);
                break;

            case "run.completed":
            case "run.failed":
                if (status == "succeeded" && type != "run.failed")
                {
                    _writer.WriteLine();
                    _writer.WriteLine(AnsiTheme.Green($"└─ [Run #{seq} Succeeded] ") + summary);
                }
                else
                {
                    _writer.WriteLine();
                    _writer.WriteLine(AnsiTheme.Red($"└─ [Run #{seq} FAILED] ") + summary);
                    if (summary.Contains("rate_limited"))
                    {
                        _writer.WriteLine(AnsiTheme.Yellow("  [Tip] Model provider rate limit exceeded (HTTP 429). Please wait a moment or switch to another model."));
                    }
                    else if (summary.Contains("auth"))
                    {
                        _writer.WriteLine(AnsiTheme.Yellow("  [Tip] Model provider authentication failed. Please verify your API key (/model)."));
                    }
                    else if (summary.Contains("timeout"))
                    {
                        _writer.WriteLine(AnsiTheme.Yellow("  [Tip] Model provider timed out. The upstream server may be overloaded."));
                    }
                    else if (summary.Contains("worker") || summary.Contains("provider") || summary.Contains("generation_failed") || summary.Contains("drive_failed"))
                    {
                        _writer.WriteLine(AnsiTheme.Yellow("  [Tip] Model provider execution failed. Run '/model' or check API settings in RuApp."));
                    }
                }
                break;

            default:
                _writer.WriteLine(AnsiTheme.Dim($"  • [{seq}] {type}: {summary}"));
                break;
        }
        _writer.Flush();
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Length > 500) s = s.Substring(0, 500) + "...";
        return s.Replace("\n", " ").Replace("\r", " ");
    }
}

public static class TuiEventRenderer
{
    public static void RenderFrame(SseFrame frame, TextWriter writer)
    {
        var renderer = new TuiSessionRenderer(writer);
        renderer.RenderFrame(frame);
    }

    public static void RenderEnvelope(HarnessSafeEventEnvelope envelope, TextWriter writer)
    {
        var renderer = new TuiSessionRenderer(writer);
        renderer.RenderEnvelope(envelope);
    }
}
