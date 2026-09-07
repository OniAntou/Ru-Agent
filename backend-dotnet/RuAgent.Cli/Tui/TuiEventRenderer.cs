using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed class TuiSessionRenderer
{
    private readonly TextWriter _writer;
    private bool _hasStreamedDelta;
    private bool _isThinking;
    private System.Diagnostics.Stopwatch? _thinkingWatch;

    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public TuiSessionRenderer(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Reset()
    {
        StopThinkingSpinner(showSummary: false);
        _hasStreamedDelta = false;
        _isThinking = false;
        _thinkingWatch?.Reset();
    }

    private void StartThinkingSpinner()
    {
        _isThinking = true;
        _thinkingWatch = System.Diagnostics.Stopwatch.StartNew();

        if (Console.IsOutputRedirected || _writer != Console.Out)
        {
            _writer.WriteLine(AnsiTheme.Dim("  • Thinking..."));
            _writer.Flush();
            return;
        }

        _spinnerCts?.Cancel();
        _spinnerCts?.Dispose();
        _spinnerCts = new CancellationTokenSource();
        var ct = _spinnerCts.Token;

        _spinnerTask = Task.Run(async () =>
        {
            int frameIdx = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var elapsed = _thinkingWatch?.Elapsed.TotalSeconds ?? 0;
                    var frameChar = SpinnerFrames[frameIdx++ % SpinnerFrames.Length];
                    _writer.Write($"\r  {AnsiTheme.Cyan(frameChar)} {AnsiTheme.Dim($"Thinking... ({elapsed:F1}s)")}  ");
                    _writer.Flush();
                    await Task.Delay(80, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void StopThinkingSpinner(bool showSummary = true)
    {
        if (!_isThinking) return;
        _isThinking = false;

        if (_spinnerCts != null)
        {
            _spinnerCts.Cancel();
            try { _spinnerTask?.Wait(200); } catch { }
            _spinnerCts.Dispose();
            _spinnerCts = null;
            _spinnerTask = null;

            if (!Console.IsOutputRedirected && _writer == Console.Out)
            {
                _writer.Write("\r                                        \r");
                _writer.Flush();
            }
        }

        if (showSummary)
        {
            var elapsed = _thinkingWatch?.Elapsed.TotalSeconds ?? 0;
            if (elapsed >= 0.4)
            {
                _writer.WriteLine(AnsiTheme.Dim($"  • Thought for {elapsed:F1}s"));
                _writer.Flush();
            }
        }
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
                StopThinkingSpinner(showSummary: true);
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
                    StartThinkingSpinner();
                }
                else if (activity.State == AgentActivityState.Completed)
                {
                    StopThinkingSpinner(showSummary: true);
                }
                else if (activity.State == AgentActivityState.Failed)
                {
                    StopThinkingSpinner(showSummary: false);
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
                var reqIcon = GetToolIcon(summary);
                _writer.WriteLine();
                _writer.WriteLine(AnsiTheme.Yellow($"{reqIcon} [Tool Request #{seq}] ") + AnsiTheme.Bold(summary));
                break;

            case "tool.executed":
                var doneIcon = GetToolIcon(summary);
                if (status == "succeeded")
                {
                    _writer.WriteLine(AnsiTheme.Green($"✓ {doneIcon} [Tool Done #{seq}] ") + summary);
                    RenderDiffIfPresent(envelope.SafeSummary);
                }
                else
                {
                    _writer.WriteLine(AnsiTheme.Red($"✗ {doneIcon} [Tool Failed #{seq}] ") + summary + $" ({status})");
                }
                break;

            case "approval.requested":
                _writer.WriteLine();
                int boxWidth = 76;
                try
                {
                    if (!Console.IsOutputRedirected && Console.WindowWidth > 20)
                    {
                        boxWidth = Math.Clamp(Console.WindowWidth - 2, 48, 76);
                    }
                }
                catch { }
                int inner = boxWidth - 2;
                string top = "╔" + new string('═', Math.Max(0, inner - 24)) + " ⚠️  APPROVAL REQUIRED " + new string('═', 2) + "╗";
                if (top.Length > boxWidth) top = "╔" + new string('═', inner) + "╗";
                string bot = "╚" + new string('═', inner) + "╝";

                string BoxLine(string text)
                {
                    var vis = text.Length > inner - 2 ? text[..(inner - 5)] + "..." : text;
                    int pad = Math.Max(0, inner - 2 - vis.Length);
                    return AnsiTheme.Yellow("║ ") + vis + new string(' ', pad) + AnsiTheme.Yellow(" ║");
                }

                _writer.WriteLine(AnsiTheme.Yellow(top));
                _writer.WriteLine(BoxLine(summary));
                _writer.WriteLine(BoxLine($"Run ID: {envelope.RunId}"));
                _writer.WriteLine(BoxLine($"Approval ID: {envelope.ApprovalId ?? ""}"));
                _writer.WriteLine(BoxLine("Actions: [y] Approve once  [r] Approve for run  [n] Deny"));
                _writer.WriteLine(AnsiTheme.Yellow(bot));
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

    private void RenderDiffIfPresent(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!text.Contains("+++") && !text.Contains("---") && !text.Contains("@@") && !text.Contains("\n+") && !text.Contains("\n-"))
            return;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("@@", StringComparison.Ordinal)
                || line.StartsWith("+", StringComparison.Ordinal)
                || line.StartsWith("-", StringComparison.Ordinal))
            {
                _writer.WriteLine("    " + AnsiTheme.FormatDiffLine(line));
            }
        }
    }

    private static string GetToolIcon(string text)
    {
        if (string.IsNullOrEmpty(text)) return "⚙";
        var lower = text.ToLowerInvariant();
        if (lower.Contains("read") || lower.Contains("files.read")) return "📖";
        if (lower.Contains("edit") || lower.Contains("files.edit") || lower.Contains("patch")) return "✏️";
        if (lower.Contains("write") || lower.Contains("files.write")) return "📝";
        if (lower.Contains("terminal") || lower.Contains("bash") || lower.Contains("cmd") || lower.Contains("shell") || lower.Contains("run")) return "🐚";
        if (lower.Contains("search") || lower.Contains("find") || lower.Contains("grep") || lower.Contains("list")) return "🔍";
        return "⚙";
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
