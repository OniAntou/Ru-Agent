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
    private System.Diagnostics.Stopwatch? _turnWatch;
    private string _lineBuf = string.Empty;
    private bool _inToolBlock;
    private bool _atLineStart = true;

    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] ThinkingVerbs = ["Thinking", "Reticulating", "Computing", "Pondering", "Percolating"];

    public static string PickThinkingVerb(TimeSpan elapsed) =>
        ThinkingVerbs[(int)(elapsed.TotalSeconds / 5) % ThinkingVerbs.Length];

    public TuiSessionRenderer(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Reset()
    {
        StopThinkingSpinner(showSummary: false);
        _hasStreamedDelta = false;
        _isThinking = false;
        _lineBuf = string.Empty;
        _inToolBlock = false;
        _atLineStart = true;
        _thinkingWatch?.Reset();
        _turnWatch?.Reset();
    }

    /// <summary>
    /// Starts the thinking indicator immediately (right after submit, before
    /// the run is even created) so Enter never meets dead air. The first real
    /// activity frame or delta takes over seamlessly (double-start guarded);
    /// <see cref="EndTurn"/> stops it on paths that never stream a frame.
    /// </summary>
    public void BeginTurn()
    {
        Reset();
        StartThinkingSpinner();
    }

    /// <summary>Silent stop for turns that ended without streaming frames. No-op when already stopped.</summary>
    public void EndTurn()
    {
        StopThinkingSpinner(showSummary: false);
    }

    private void StartThinkingSpinner()
    {
        _isThinking = true;
        _thinkingWatch = System.Diagnostics.Stopwatch.StartNew();

        if (Console.IsOutputRedirected || _writer != Console.Out)
        {
            _writer.WriteLine(AnsiTheme.Dim("  Thinking…"));
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
                    var elapsed = _thinkingWatch?.Elapsed ?? TimeSpan.Zero;
                    var frameChar = SpinnerFrames[frameIdx++ % SpinnerFrames.Length];
                    var verb = PickThinkingVerb(elapsed);
                    _writer.Write($"\r  {AnsiTheme.White(frameChar)} {AnsiTheme.White(verb)}… {AnsiTheme.Silver($"({elapsed.TotalSeconds:F1}s)")}  ");
                    _writer.Flush();
                    await Task.Delay(90, ct).ConfigureAwait(false);
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
                _writer.WriteLine(AnsiTheme.Dim($"  ◆ Thought for {elapsed:F1}s"));
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
                _atLineStart = false;
            }

            RenderDeltaText(delta.Text);
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
                    _writer.WriteLine(AnsiTheme.Red("  ◆ Model thinking failed."));
                    _writer.Flush();
                }
            }
        }
    }

    /// <summary>
    /// Streams assistant text while restyling tool-call payloads (dim gutter,
    /// markers swallowed) wherever they appear — the model often glues
    /// &lt;tool_call&gt; onto the end of a chat sentence, so markers are
    /// scanned inline, not just as whole lines. Only a trailing partial
    /// marker (at most 11 chars) is held back for the next chunk; everything
    /// else paints immediately, byte-identical to before.
    /// </summary>
    private void RenderDeltaText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lineBuf += text;
        var hold = PartialMarkerSuffixLength(_lineBuf);
        var emitLength = _lineBuf.Length - hold;
        if (emitLength <= 0) return;
        var emit = _lineBuf[..emitLength];
        _lineBuf = _lineBuf[emitLength..];
        RenderStyled(emit);
        _writer.Flush();
    }

    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    private static int PartialMarkerSuffixLength(string buffer)
    {
        var max = Math.Min(buffer.Length, Math.Max(ToolCallOpen.Length, ToolCallClose.Length) - 1);
        for (var len = max; len >= 1; len--)
        {
            var tail = buffer[^len..];
            if (ToolCallOpen.StartsWith(tail, StringComparison.Ordinal)
                || ToolCallClose.StartsWith(tail, StringComparison.Ordinal))
            {
                return len;
            }
        }
        return 0;
    }

    private void RenderStyled(string segment)
    {
        var rest = segment;
        while (rest.Length > 0)
        {
            var openIndex = rest.IndexOf(ToolCallOpen, StringComparison.Ordinal);
            var closeIndex = rest.IndexOf(ToolCallClose, StringComparison.Ordinal);
            int markerIndex;
            bool isOpen;
            if (openIndex >= 0 && (closeIndex < 0 || openIndex < closeIndex))
            {
                markerIndex = openIndex;
                isOpen = true;
            }
            else if (closeIndex >= 0)
            {
                markerIndex = closeIndex;
                isOpen = false;
            }
            else
            {
                WriteStyledText(rest);
                return;
            }

            if (markerIndex > 0)
            {
                WriteStyledText(rest[..markerIndex]);
            }
            if (!_atLineStart)
            {
                _writer.WriteLine();
                _atLineStart = true;
            }
            _inToolBlock = isOpen;
            rest = rest[(markerIndex + (isOpen ? ToolCallOpen.Length : ToolCallClose.Length))..];
        }
    }

    private void WriteStyledText(string text)
    {
        if (text.Length == 0) return;
        if (!_inToolBlock)
        {
            _writer.Write(text);
            _atLineStart = text.EndsWith('\n');
            return;
        }
        var start = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', start);
            var line = nl < 0 ? text[start..] : text[start..nl];
            if (line.Length > 0)
            {
                if (_atLineStart)
                {
                    _writer.Write(AnsiTheme.Dim("┃ "));
                }
                _writer.Write(AnsiTheme.Dim(line));
                _atLineStart = false;
            }
            if (nl < 0) return;
            _writer.WriteLine();
            _atLineStart = true;
            start = nl + 1;
            if (start >= text.Length) return;
        }
    }

    /// <summary>
    /// Emits any buffered remainder (a marker split across chunks, or trailing
    /// text without a newline). Called on every durable envelope and at turn
    /// end so text is never stuck buffered.
    /// </summary>
    public void Flush()
    {
        if (_lineBuf.Length == 0) return;
        var rest = _lineBuf;
        _lineBuf = string.Empty;
        RenderStyled(rest);
        _writer.Flush();
    }

    public void RenderEnvelope(HarnessSafeEventEnvelope envelope)
    {
        Flush();
        var seq = envelope.Sequence;
        var type = envelope.EventType;
        var status = envelope.Status;
        var summary = Escape(envelope.SafeSummary);

        // Filter out noisy state machine transitions
        if (type is "run.state_changed" or "model.step_started" or "model.step_completed" or "prompt_receipt"
            or "model.usage_recorded")
            return;

        if (_hasStreamedDelta)
        {
            _writer.WriteLine();
            _hasStreamedDelta = false;
        }

        switch (type)
        {
            case "model.turn_started":
                _turnWatch = System.Diagnostics.Stopwatch.StartNew();
                _writer.WriteLine(AnsiTheme.Cyan($"┌─ ◆ Turn #{seq}"));
                break;

            case "tool.execution_requested":
                var (reqVerb, reqDetail) = SplitToolLine(summary);
                _writer.WriteLine();
                _writer.WriteLine(AnsiTheme.Gray(reqVerb) + " " + AnsiTheme.LightBlue(reqDetail));
                break;

            case "tool.executed":
                var (doneVerb, doneDetail) = SplitToolLine(summary);
                if (status == "succeeded")
                {
                    _writer.WriteLine(AnsiTheme.BrightGreen("◆ ") + AnsiTheme.Silver(AnsiTheme.Bold($"{doneVerb} {doneDetail}")));
                    RenderDiffIfPresent(envelope.SafeSummary);
                }
                else
                {
                    _writer.WriteLine(AnsiTheme.Red($"✗ {doneVerb} ") + doneDetail + $" ({status})");
                }
                break;

            case "approval.requested":
                _writer.WriteLine();
                var gutter = AnsiTheme.Gray("┃ ");
                _writer.WriteLine(gutter + AnsiTheme.Silver(AnsiTheme.Bold(summary)));
                _writer.WriteLine(gutter + AnsiTheme.Gray($"Run: {envelope.RunId}"));
                _writer.WriteLine(gutter + AnsiTheme.White(AnsiTheme.Bold("1 (●) ")) + AnsiTheme.Silver("Yes, proceed (once)"));
                _writer.WriteLine(gutter + AnsiTheme.Gray("2 (○) No, deny"));
                _writer.WriteLine(AnsiTheme.Dim("  1/2:select │ Ctrl+c:cancel"));
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
                if ((status == "succeeded" || status == "completed") && type != "run.failed")
                {
                    var turnSecs = _turnWatch?.Elapsed.TotalSeconds ?? 0;
                    _turnWatch?.Reset();
                    _writer.WriteLine();
                    _writer.WriteLine(AnsiTheme.Green("◆stop"));
                    _writer.WriteLine(AnsiTheme.Dim($"Turn completed in {turnSecs:F1}s ") + summary);
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
                _writer.WriteLine(AnsiTheme.Gray("┃ ") + AnsiTheme.FormatDiffLine(line));
            }
        }
    }

    private static (string Verb, string Detail) SplitToolLine(string text)
    {
        var lower = text.ToLowerInvariant();
        string verb = lower.Contains("edit") || lower.Contains("patch") ? "edit"
            : lower.Contains("write") ? "write"
            : lower.Contains("read") ? "read"
            : lower.Contains("terminal") || lower.Contains("bash") || lower.Contains("shell") || lower.Contains("cmd") || lower.Contains("run") ? "bash"
            : lower.Contains("search") || lower.Contains("find") || lower.Contains("grep") || lower.Contains("list") ? "search"
            : "tool";
        var detail = text;
        var pathIdx = text.IndexOf("path:", StringComparison.OrdinalIgnoreCase);
        if (pathIdx >= 0)
        {
            detail = text[(pathIdx + 5)..].Trim();
        }

        if (detail.Length > 120)
        {
            detail = detail[..117] + "...";
        }

        return (verb, detail);
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
