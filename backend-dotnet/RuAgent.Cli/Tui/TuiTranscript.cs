using System.Diagnostics;
using System.Text;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public enum TuiTurnOutcome
{
    Completed,
    Failed,
    Cancelled,
}

public enum ToolLineStatus
{
    Ok,
    Failed,
    Running,
    Unknown,
}

/// <summary>
/// Turns the event stream into finalised transcript lines. Durable events own completion
/// records and their order. Typed activity supplies tool outcome/display metadata;
/// without it a result stays neutral, since an envelope's run status is not a tool result.
/// </summary>
public sealed partial class TuiTranscript
{
    private const int MaxDiffLines = 12;
    private const int MaxRetainedLines = 200;
    private const int ProsePreviewIntervalMilliseconds = 33;
    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    /// <summary>
    /// Cap on the raw assistant text retained per turn for gap detection against the run's
    /// durable assistant payload. Matches the in-process model step's own text cap.
    /// </summary>
    private const int MaxRetainedAssistantCharacters = 1_000_000;

    private readonly TuiSurface _surface;
    private readonly TuiStatusLine _status;
    private readonly TuiMarkdown _markdown = new();
    private readonly List<ToolDetail> _pendingTools = [];
    private readonly Queue<string> _settledActivityIds = new();
    private readonly Dictionary<string, ToolDetail> _settledToolRows = new(StringComparer.Ordinal);
    private readonly List<string> _retained = [];
    private readonly StringBuilder _streamedAssistant = new();
    private readonly Stopwatch _turnWatch = new();

    private bool _hasStreamedDelta;
    private string _lineBuffer = string.Empty;
    private readonly StringBuilder _proseLine = new();
    private StreamingMarkdownProjector? _streamingMarkdown;
    private long _lastProsePreview;
    private bool _inToolBlock;
    private bool _atLineStart = true;
    private int _toolsSettled;
    private string? _terminalCode;
    private string? _summaryDetail;
    private bool _sawCompletion;
    private bool _paused;
    private string? _pausedRunId;
    private string? _lastParkReason;
    private readonly Dictionary<string, string> _parkReasons = new(StringComparer.Ordinal);
    private bool _repeatedPark;
    private bool _fullScreenCards;
    private string? _pendingPlanProgress;
    private string? _lastPlanProgress;

    // Tool-call grouping: consecutive settled tool rows form one feed block. The summary is
    // written when the block closes, because terminal output is append-only in both sinks.
    private bool _toolGroupOpen;
    private int _toolGroupCount;
    private double _toolGroupStarted;
    private bool _toolGroupFailed;

    /// <summary>
    /// Full-screen transcript rendering: tool results become inline <see cref="TuiToolRow"/>
    /// rows in the OpenCode style with the diff hunks behind <c>ctrl+o</c>, and fenced code
    /// goes through <see cref="TuiCodeHighlight"/>. Off by default so the sequential fallback
    /// keeps its existing rows byte-for-byte.
    /// </summary>
    public bool FullScreenCards
    {
        get => _fullScreenCards;
        set
        {
            _fullScreenCards = value;
            _markdown.HighlightCode = value;
        }
    }

    /// <summary>
    /// The raw assistant text this turn actually received as deltas, before markdown
    /// rendering. Deltas published before the client subscribes are gone for good -- the
    /// live hub has no replay buffer -- so this is what a gap check compares the run's
    /// durable assistant payload against.
    /// </summary>
    public string StreamedAssistantText => _streamedAssistant.ToString();

    /// <summary>
    /// Rows behind the last <c>ctrl+o</c> affordance, still palette-painted. The Terminal.Gui
    /// bridge reads this when a summary row is committed so its transcript can expand the
    /// block; the append-only sink has no collapsed blocks.
    /// </summary>
    public IReadOnlyList<string> RetainedLines => _retained;

    /// <summary>
    /// Rewrites this turn's assistant rows with the run's complete text through the sink's
    /// turn rewriter (the Terminal.Gui bridge). Returns false for a plain text sink, which
    /// cannot unprint rows; the caller then reports the truncation instead of guessing.
    /// </summary>
    public async Task<bool> RecoverAssistantTurnAsync(string fullText)
    {
        if (_surface.Writer is not ITranscriptTurnRewriter screen) return false;
        fullText = TuiText.SanitizeUntrusted(fullText);
        if (fullText.Length == 0) return false;
        Flush();
        var markdown = new TuiMarkdown { HighlightCode = _fullScreenCards };
        bool replaced;
        if (_surface.Presentation is { } presentation)
        {
            var rows = fullText.Replace("\r", string.Empty).Split('\n')
                .SelectMany(line => markdown.RenderPresentation(line, Width)).ToArray();
            replaced = await presentation.ReplaceAssistantPresentationAsync(rows).ConfigureAwait(false);
        }
        else
        {
            var rows = fullText.Replace("\r", string.Empty).Split('\n')
                .SelectMany(line => markdown.RenderLogicalLine(line, Width)).ToArray();
            replaced = await screen.ReplaceAssistantTurnAsync(rows).ConfigureAwait(false);
        }
        if (!replaced) return false;
        _streamedAssistant.Clear();
        _streamedAssistant.Append(fullText);
        return true;
    }

    public TuiTranscript(TuiSurface surface, TuiStatusLine status)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        Width = TuiViewport.Width();
    }

    /// <summary>Viewport width used for finalized blocks; the CLI keeps this current on resize.</summary>
    public int Width { get; set; }

    /// <summary>Tool rows settled in the turn in progress (or the last completed turn).</summary>
    public int ToolsSettled => _toolsSettled;
    public double ElapsedSeconds => _turnWatch.Elapsed.TotalSeconds;

    /// <summary>
    /// Writes finished lines and repaints the live row in the same lock acquisition, so the
    /// spinner can never interleave between two transcript lines.
    /// </summary>
    private void WriteLines(params string[] lines)
    {
        if (_surface.Presentation is not null)
        {
            foreach (var line in lines) _surface.AppendPresentation(TranscriptLine.Plain(TuiText.SanitizeUntrusted(line), TranscriptRole.Meta));
        }
        else _surface.WriteBlock(lines);
    }

    private sealed record ToolDetail(
        string ActivityId,
        AgentActivityState State,
        string Verb,
        string LiveVerb,
        string? Display,
        string? Target,
        int? Count,
        int? Added,
        int? Removed,
        int? DurationMs);

    public void BeginTurn()
    {
        _toolsSettled = 0;
        _pendingPlanProgress = null;
        _lastPlanProgress = null;
        _hasStreamedDelta = false;
        _lineBuffer = string.Empty;
        _proseLine.Clear();
        _lastProsePreview = 0;
        _inToolBlock = false;
        _atLineStart = true;
        _terminalCode = null;
        _summaryDetail = null;
        _sawCompletion = false;
        _paused = false;
        _pausedRunId = null;
        _lastParkReason = null;
        _repeatedPark = false;
        _streamingMarkdown?.Reset();
        _pendingTools.Clear();
        _settledActivityIds.Clear();
        _settledToolRows.Clear();
        _retained.Clear();
        _markdown.Reset();
        _turnWatch.Restart();
        _streamedAssistant.Clear();
        _toolGroupOpen = false;
        _toolGroupCount = 0;
        _toolGroupFailed = false;

        // A blank row separates the turn from what came before. It is also the first write
        // of the turn: an output that fails here must still unwind through the restore path
        // before any run starts.
        _surface.Line();
    }

    /// <summary>
    /// Resets the logical transcript state for <c>/clear</c> without writing a turn
    /// separator. The Terminal.Gui view clears its own rows; this drops the renderer's
    /// retained details, tool counters, buffered prose and markdown state so the next
    /// <c>/status</c>, Ctrl+O or stream starts from an empty session view. A parked run is
    /// session state, not transcript state, so its pause flag is retained.
    /// </summary>
    public void ClearView()
    {
        _retained.Clear();
        _pendingTools.Clear();
        _settledActivityIds.Clear();
        _settledToolRows.Clear();
        _streamedAssistant.Clear();
        _lineBuffer = string.Empty;
        _proseLine.Clear();
        _streamingMarkdown?.Reset();
        _lastProsePreview = 0;
        _inToolBlock = false;
        _atLineStart = true;
        _hasStreamedDelta = false;
        _terminalCode = null;
        _summaryDetail = null;
        _sawCompletion = false;
        _pendingPlanProgress = null;
        _lastPlanProgress = null;
        _toolsSettled = 0;
        _toolGroupOpen = false;
        _toolGroupCount = 0;
        _toolGroupFailed = false;
        _markdown.Reset();
        _turnWatch.Reset();
        if (!_paused)
        {
            _status.Reset();
        }
    }

    // ------------------------------------------------------------------
    // Streaming assistant text (markers swallowed, block text guttered).
    // ------------------------------------------------------------------

    public void AppendAssistant(string text)
    {
        // Model deltas are untrusted terminal input. Strip escape/control content before it
        // reaches either sink; a sequence split across two deltas is neutralised because the
        // leading ESC is dropped and the remaining bytes render as harmless text.
        var safeText = TuiText.SanitizeUntrusted(text);
        if (safeText.Length == 0)
        {
            return;
        }
        if (_status.State == TuiRunState.Thinking) _status.SetResponding();

        if (_streamedAssistant.Length < MaxRetainedAssistantCharacters)
        {
            _streamedAssistant.Append(safeText);
        }

        if (!_hasStreamedDelta)
        {
            _hasStreamedDelta = true;
            // The spacer row separates this block from what came before (the user block or
            // a settled tool row).
            if (_atLineStart)
            {
                _surface.Line();
            }

            _atLineStart = true;
        }

        _lineBuffer += safeText;
        // Both marker families are held back: a tool-call wrapper and a markdown emphasis
        // can each be cut in half by a delta boundary.
        var hold = Math.Max(PartialMarkerSuffixLength(_lineBuffer), TuiMarkdown.Holdback(_lineBuffer));
        var emitLength = _lineBuffer.Length - hold;
        if (emitLength <= 0)
        {
            return;
        }

        var emit = _lineBuffer[..emitLength];
        _lineBuffer = _lineBuffer[emitLength..];
        RenderStyled(emit);
        _surface.Flush();
    }

    /// <summary>
    /// Drops text held back for the current streamed answer. A retried model step
    /// replaces the attempt that failed, so the pending buffer must not leak into
    /// the new attempt's text. Text already flushed to the surface cannot be
    /// un-written in a terminal; it is superseded by the retry's output.
    /// </summary>
    public void ResetAssistant() => ResetAssistantCore(clearRawText: true);

    /// <summary>
    /// Drops the pending render state of the current answer while keeping its raw text
    /// in gap tracking. A host-suppressed step is superseded before its unfinished tail
    /// ever completed: the held buffer must not leak into the answer that replaces it,
    /// but the raw text stays, because dropping it would make the whole durable answer
    /// look missing and the turn-end recovery would reprint it.
    /// </summary>
    public void DiscardAssistantTail() => ResetAssistantCore(clearRawText: false);

    private void ResetAssistantCore(bool clearRawText)
    {
        if (_status.State == TuiRunState.Thinking) _status.SetState(TuiRunState.Thinking);
        // A superseded attempt's answer is replaced by the retry: drop its pending
        // render buffer, and its raw text from gap tracking, so the retry -- not the
        // stale head it replaced -- is what gets compared against the durable payload.
        // Already rendered rows cannot be un-written; the retry supersedes them visually.
        // The turn's block marker is intentionally kept so a later repaint can still
        // target the whole turn, and the header is not reprinted.
        _lineBuffer = string.Empty;
        _proseLine.Clear();
        _streamingMarkdown?.Reset();
        _lastProsePreview = 0;
        _surface.PreviewLine(string.Empty);
        if (clearRawText)
        {
            _streamedAssistant.Clear();
        }

        _markdown.Reset();
    }

    /// <summary>An operator-facing one-liner (approval outcomes, reconciliation prompts).</summary>
    public void Note(string text, TuiToken token) => Notice(text, token);

    /// <summary>
    /// The model's own deliberation is kept out of the transcript and behind <c>ctrl+o</c>.
    /// The prompt contract calls private reasoning something never to surface in chat, and
    /// the WinUI surface keeps the same block closed by default; printing it inline buried
    /// the answer the operator actually came to read.
    /// </summary>
    private void RetainThinking(string detail)
    {
        var flat = Escape(detail);
        if (flat.Length == 0)
        {
            return;
        }

        // Thinking detail belongs to the row that advertises it: replace, never append, so
        // a later thought does not inherit the previous block.
        _retained.Clear();
        foreach (var line in flat.Split('\n'))
        {
            var bounded = TuiText.SafeCut(line, 16_000);
            _retained.Add("    " + (_surface.Presentation is not null ? bounded : TuiPalette.DimPaint(bounded)));
        }

        TrimRetained();
        if (_fullScreenCards && _surface.Writer is ITranscriptLineWriter screen)
        {
            Flush();
            screen.RetainDetails(_retained.ToArray());
        }
        else Notice("Thought · ctrl+o", TuiToken.Meta);
    }

    private void Notice(string text, TuiToken token)
    {
        CloseToolGroup();
        Flush();
        if (!_atLineStart)
        {
            _surface.Line();
            _atLineStart = true;
        }

        var body = Escape(text);
        var bodyToken = token == TuiToken.Line ? TuiToken.Meta : token;
        if (_surface.Presentation is not null)
        {
            _surface.AppendPresentation(new TranscriptRunBuilder()
                .Append("  · ", TuiToken.Line)
                .Append(body, bodyToken)
                .Line(TranscriptRole.Meta));
        }
        else
        {
            WriteLines(TuiPalette.Paint("  · ", TuiToken.Line) + TuiPalette.Paint(body, bodyToken));
        }
    }

    public void ApplyDiffText(string? text, int width)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        text = TuiText.SanitizeUntrusted(text);
        var lines = text.Split('\n');
        var diffLines = lines.Where(IsDiffLine).ToList();
        if (diffLines.Count == 0)
        {
            return;
        }

        foreach (var line in diffLines.Take(MaxDiffLines))
        {
            if (_surface.Presentation is not null)
            {
                var header = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal);
                var token = header ? TuiToken.Ink : line.StartsWith("@@", StringComparison.Ordinal) ? TuiToken.Brand
                    : line.StartsWith('+') ? TuiToken.Ok : line.StartsWith('-') ? TuiToken.Err : TuiToken.Ink;
                _surface.AppendPresentation(new TranscriptRunBuilder().Append("    ").Append("│ ", TuiToken.Line)
                    .Append(line, token, bold: header).Line(TranscriptRole.Meta));
            }
            else WriteLines("    " + TuiPalette.Paint("│ ", TuiToken.Line) + FormatDiffLine(line));
        }

        if (diffLines.Count > MaxDiffLines)
        {
            _retained.Clear();
            foreach (var line in diffLines)
            {
                _retained.Add("    " + RetainedPaint("│ ", TuiToken.Line)
                    + (_surface.Presentation is not null ? line : FormatDiffLine(line)));
            }

            TrimRetained();
            var more = $"    │ · {diffLines.Count - MaxDiffLines} more lines · ctrl+o";
            if (_surface.Presentation is not null)
                _surface.AppendPresentation(TranscriptLine.Plain(more, TranscriptRole.Meta, TuiToken.Line));
            else WriteLines(TuiPalette.Paint(more, TuiToken.Line));
        }

        _ = width;
    }

    private void TrimRetained()
    {
        while (_retained.Count > MaxRetainedLines)
        {
            _retained.RemoveAt(0);
        }
    }

    public void ToggleExpand()
    {
        if (_retained.Count == 0)
        {
            Notice("nothing to expand", TuiToken.Line);
            return;
        }

        Flush();
        var block = new List<string>(_retained)
        {
            TuiPalette.Paint("    │ · end of expanded block", TuiToken.Line),
        };
        _surface.WriteBlock(block);
    }

    private static bool IsDiffLine(string line) =>
        line.StartsWith("+++", StringComparison.Ordinal)
        || line.StartsWith("---", StringComparison.Ordinal)
        || line.StartsWith("@@", StringComparison.Ordinal)
        || line.StartsWith("+", StringComparison.Ordinal)
        || line.StartsWith("-", StringComparison.Ordinal);

    public static string FormatDiffLine(string line)
    {
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
        {
            return TuiPalette.BoldPaint(line);
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return TuiPalette.Paint(line, TuiToken.Brand);
        }

        if (line.StartsWith("+", StringComparison.Ordinal))
        {
            return TuiPalette.Paint(line, TuiToken.Ok);
        }

        if (line.StartsWith("-", StringComparison.Ordinal))
        {
            return TuiPalette.Paint(line, TuiToken.Err);
        }

        return line;
    }

    // ------------------------------------------------------------------
    // Turn summary and outcome.
    // ------------------------------------------------------------------

    public void ApplySummary(AgentRunSummaryWire summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var parts = new List<string>();
        var files = summary.FilesChanged ?? 0;
        if (files > 0)
        {
            parts.Add(files == 1 ? "1 file changed" : $"{files} files changed");
        }

        if (summary.FilesCreated is > 0)
        {
            parts.Add($"{summary.FilesCreated} created");
        }

        if (summary.FilesDeleted is > 0)
        {
            parts.Add($"{summary.FilesDeleted} deleted");
        }

        if (!string.IsNullOrWhiteSpace(summary.VerificationState)
            && !string.Equals(summary.VerificationState, "not_applicable", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(summary.VerificationState);
        }

        _summaryDetail = parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Called by the CLI on every terminal path. The outcome is derived from the durable
    /// stream: a recorded failure prints the failure block, a recorded completion prints the
    /// turn footer, and a turn aborted before any terminal event stops silently (the caller
    /// already printed its own notice).
    /// </summary>
    public void EndTurn()
    {
        CloseToolGroup();
        Flush();
        if (_terminalCode is not null)
        {
            EndTurn(TuiTurnOutcome.Failed, _terminalCode);
            return;
        }

        if (_sawCompletion)
        {
            EndTurn(TuiTurnOutcome.Completed, null);
            return;
        }

        if (_hasStreamedDelta)
        {
            if (!_atLineStart)
            {
                _surface.Line();
            }
            _surface.Line();
            _hasStreamedDelta = false;
            _atLineStart = true;
        }

        _turnWatch.Reset();
        _status.Reset();
        _status.SetState(TuiRunState.Idle);
    }

    public void EndTurn(TuiTurnOutcome outcome, string? errorCode)
    {
        CloseToolGroup();
        Flush();
        var streamedText = _hasStreamedDelta;
        if (_hasStreamedDelta)
        {
            if (!_atLineStart)
            {
                _surface.Line();
            }
            _surface.Line();
            _hasStreamedDelta = false;
            _atLineStart = true;
        }
        else if (_toolsSettled > 0 && _atLineStart)
        {
            _surface.Line();
        }

        var seconds = _turnWatch.Elapsed.TotalSeconds;
        _turnWatch.Reset();
        _status.Reset();
        _status.SetState(outcome == TuiTurnOutcome.Failed ? TuiRunState.Failed : TuiRunState.Idle);
        _status.SetElapsed(outcome == TuiTurnOutcome.Completed ? seconds : 0);
        _status.SetDetail(outcome == TuiTurnOutcome.Completed ? _summaryDetail : null);

        if (_surface.Presentation is not null) WritePresentationOutcome(outcome, errorCode, seconds, streamedText);
        else switch (outcome)
        {
            case TuiTurnOutcome.Completed:
                WriteLines(BuildFooter(seconds, _toolsSettled, _summaryDetail));
                if (!streamedText && _toolsSettled == 0)
                {
                    // A run that completed with no assistant text and no tool row used to
                    // end in total silence: just a "✓ idle · 10.6s" row, which is
                    // indistinguishable from a healthy short answer. The reply text is
                    // persisted even when its delta never reaches this client, so say so
                    // rather than leaving the operator to guess.
                    WriteLines(ComposeNoTextArrived());
                }

                break;
            case TuiTurnOutcome.Cancelled:
                WriteLines(
                    TuiPalette.Paint("  ✗ ", TuiToken.Err)
                    + TuiPalette.Paint($"{TuiStatusLine.FormatElapsed(seconds)} · cancelled", TuiToken.Meta));
                break;
            default:
                _surface.WriteBlock(ComposeFailure(seconds, errorCode ?? _terminalCode ?? "run_failed"));
                break;
        }

        _toolsSettled = 0;
        _terminalCode = null;
        _summaryDetail = null;
        _pendingTools.Clear();
        // The turn is fully wound down: a second stop (the documented no-op path)
        // must not re-read the completion flag and print a second footer.
        _sawCompletion = false;
    }

    /// <summary>
    /// Bounded operator note for a turn that finished successfully but delivered no
    /// assistant text and no tool row. Names the observation and the next step instead
    /// of leaving an empty turn that looks like a very short answer.
    /// </summary>
    public static string ComposeNoTextArrived() =>
        TuiPalette.Paint("  ▲ ", TuiToken.Warn)
        + TuiPalette.Paint("no text arrived from the model", TuiToken.Warn)
        + TuiPalette.Paint(
            " · the run completed but its reply never reached this terminal · retry, or switch model with /model",
            TuiToken.Line);

    public static string BuildFooter(double seconds, int tools, string? detail)
    {
        var parts = new List<string>
        {
            TuiStatusLine.FormatElapsed(seconds),
            tools == 1 ? "1 tool" : $"{tools} tools",
        };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail);
        }

        return TuiPalette.Paint("  ✓ ", TuiToken.Ok)
            + TuiPalette.Paint(string.Join(" · ", parts), TuiToken.Meta);
    }

    /// <summary>Failure card: title plus cause and fix lines from the failure catalog.</summary>
    public static IReadOnlyList<string> ComposeFailure(double seconds, string code)
    {
        var info = TuiFailureCatalog.For(code);
        return
        [
            TuiPalette.Paint("  ✗ ", TuiToken.Err)
            + TuiPalette.Paint($"{TuiStatusLine.FormatElapsed(seconds)} · ", TuiToken.Meta)
            + TuiPalette.Paint(info.Title, TuiToken.Err)
            + TuiPalette.Paint($" ({info.Code})", TuiToken.Meta),
            TuiPalette.Paint("    " + info.Cause, TuiToken.Line),
            TuiPalette.Paint("    " + info.Fix, TuiToken.Line),
        ];
    }

    public static string? Remedy(string code) =>
        TuiFailureCatalog.TryGet(code, out var info) ? info.Fix : null;

    private static string FormatDuration(int durationMs) =>
        durationMs < 1000
            ? $"{durationMs}ms"
            : (durationMs / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s";

    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = TuiText.SanitizeUntrusted(text);
        if (text.Length > 500)
        {
            text = TuiText.SafeCut(text, 500) + "…";
        }

        return text.Replace("\r", " ").Replace("\n", " ");
    }
}
