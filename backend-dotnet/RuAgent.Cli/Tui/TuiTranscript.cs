using System.Diagnostics;
using System.Text;
using RuAgent.Client;
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
    private bool _retainedIsThinking;
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
        _retainedIsThinking = false;
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
        _retainedIsThinking = false;
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

    public void Flush()
    {
        if (_lineBuffer.Length > 0)
        {
            var rest = _lineBuffer;
            _lineBuffer = string.Empty;
            RenderStyled(rest);
        }
        FlushProseLine();
        // Metadata must not split an unfinished word. Publish progress only at an
        // existing transcript boundary, retaining counts rather than a second task list.
        if (_pendingPlanProgress is { } progress && progress != _lastPlanProgress)
        {
            CloseToolGroup();
            if (_surface.Presentation is not null)
                _surface.AppendPresentation(TranscriptLine.Plain("  · " + progress, TranscriptRole.Meta, TuiToken.Meta));
            else WriteLines(TuiPalette.Paint("  · " + progress, TuiToken.Meta));
            _lastPlanProgress = progress;
        }
        _pendingPlanProgress = null;
        _surface.Flush();
    }

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
                FlushProseLine();
                if (_inToolBlock) _surface.Line();
                _atLineStart = true;
            }

            // A buffered paragraph must not survive a tool block: its row would land after
            // the tool rows it preceded.
            FlushParagraph();

            // Prose on both sides of a tool block is independent text: a fence open when the
            // tool call started cannot survive it.
            _markdown.Reset();
            _inToolBlock = isOpen;
            rest = rest[(markerIndex + (isOpen ? ToolCallOpen.Length : ToolCallClose.Length))..];
        }
    }

    private void WriteStyledText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (!_inToolBlock)
        {
            WriteProse(text);
            return;
        }

        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            var line = newline < 0 ? text[start..] : text[start..newline];
            if (line.Length > 0)
            {
                if (_surface.Presentation is not null)
                {
                    var row = new TranscriptRunBuilder();
                    if (_atLineStart) row.Append("  │ ", TuiToken.Line);
                    _surface.AppendPresentationChunk(row.Append(line, dim: true).Line(TranscriptRole.Meta));
                }
                else
                {
                    if (_atLineStart) _surface.Write(TuiPalette.Paint("  │ ", TuiToken.Line));
                    _surface.Write(TuiPalette.DimPaint(line));
                }
                _atLineStart = false;
            }

            if (newline < 0)
            {
                return;
            }

            _surface.Line();
            _atLineStart = true;
            start = newline + 1;
            if (start >= text.Length)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Prose leaves the surface through the markdown renderer. A completed line goes
    /// through <see cref="TuiMarkdown.RenderLine"/> so block forms (fence, bullet, heading)
    /// are decided from the complete raw line. A screen previews the unfinished tail
    /// in place without changing parser state; plain output waits for newline/Flush.
    /// </summary>
    private void WriteProse(string text)
    {
        if (_surface.Presentation is { } presentation)
        {
            // Tool metadata must settle before any provisional paragraph rows, not
            // between its frozen prefix and the tail when the source line completes.
            CloseToolGroup();
            _streamingMarkdown ??= new StreamingMarkdownProjector(_markdown,
                presentation.PreviewPresentation, _surface.CommitPresentation);
            _streamingMarkdown.Append(text, Width);
            _atLineStart = text.EndsWith('\n');
            return;
        }
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                if (start < text.Length)
                {
                    _proseLine.Append(text.AsSpan(start));
                    // Parse the growing tail at frame cadence, not for every token.
                    // The first fragment and completed line are always published.
                    if (_surface.HasLineProjection && (_proseLine.Length == text.Length - start
                        || Stopwatch.GetElapsedTime(_lastProsePreview).TotalMilliseconds >= ProsePreviewIntervalMilliseconds))
                    {
                        var partial = _proseLine.ToString();
                        _surface.PreviewLine(_markdown.PreviewLine(partial, Width));
                        _lastProsePreview = Stopwatch.GetTimestamp();
                    }
                    _atLineStart = false;
                }

                return;
            }

            _proseLine.Append(text.AsSpan(start, newline - start));
            WriteCompleteLine(_proseLine.ToString());
            _proseLine.Clear();
            start = newline + 1;
            if (start >= text.Length)
            {
                _atLineStart = true;
                return;
            }
        }
    }

    private void WriteCompleteLine(string line)
    {
        // Prose ends a tool block: the summary lands before the first line of the reply.
        CloseToolGroup();
        var rows = _surface.HasLineProjection
            ? _markdown.RenderLogicalLine(line, Width)
            : _markdown.RenderLine(line, Width);
        _surface.CommitProjectedLine(rows);
    }

    /// <summary>
    /// Commits native paragraph chunks before another block interrupts them.
    /// </summary>
    private void FlushParagraph()
    {
        _streamingMarkdown?.FlushParagraph();
    }

    private void FlushProseLine()
    {
        if (_surface.Presentation is not null)
        {
            _streamingMarkdown?.Flush(Width);
            _atLineStart = true;
            return;
        }
        if (_proseLine.Length > 0)
        {
            WriteCompleteLine(_proseLine.ToString());
            _proseLine.Clear();
        }

        // The unfinished line closes with the block it belonged to.
        FlushParagraph();
        _atLineStart = true;
    }

    // ------------------------------------------------------------------
    // Activity frames: detail for the live row and for the settled tool line.
    // ------------------------------------------------------------------

    public void ApplyActivity(AgentActivityModel activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        switch (activity.Kind)
        {
            case AgentActivityKind.Thinking:
                if (activity.State == AgentActivityState.Started)
                {
                    _status.SetState(TuiRunState.Thinking);
                }
                else if (!string.IsNullOrWhiteSpace(activity.Detail))
                {
                    RetainThinking(activity.Detail);
                }

                return;

            case AgentActivityKind.Compaction:
                if (activity.State is AgentActivityState.Started or AgentActivityState.Waiting)
                {
                    _status.SetState(TuiRunState.Thinking);
                    return;
                }
                if (activity.State == AgentActivityState.Failed)
                {
                    Notice($"compaction failed ({activity.SafeCode})", TuiToken.Err);
                }
                else
                {
                    var parts = new List<string> { "context compacted" };
                    if (activity.Count is { } count)
                    {
                        parts.Add(count == 1 ? "1 item" : $"{count} items");
                    }

                    if (activity.DurationMs is { } duration)
                    {
                        parts.Add(FormatDuration(duration));
                    }

                    Notice(string.Join(" · ", parts), TuiToken.Brand2);
                }

                return;

            case AgentActivityKind.Retry:
                Notice($"retrying {RetryTarget(activity)}", TuiToken.Warn);
                return;

            case AgentActivityKind.Status:
                if (!string.IsNullOrWhiteSpace(activity.ToolDisplay))
                {
                    ApplyToolActivity(activity);
                    return;
                }
                if (activity.SafeCode != "frames_dropped")
                {
                    Notice(activity.SafeCode, TuiToken.Line);
                }

                return;

            case AgentActivityKind.Todo:
                if (activity.Todos is { } todos)
                    _pendingPlanProgress = $"Plan · {todos.Count(todo => todo.Status == "completed")}/{todos.Count} complete";
                if (!string.IsNullOrWhiteSpace(activity.ToolDisplay)) ApplyToolActivity(activity);
                return;

            case AgentActivityKind.Explore:
            case AgentActivityKind.Search:
            case AgentActivityKind.Analyze:
            case AgentActivityKind.Edit:
            case AgentActivityKind.Terminal:
                ApplyToolActivity(activity);
                return;

            default:
                return;
        }
    }

    private void ApplyToolActivity(AgentActivityModel activity)
    {
        var verb = TuiToolVerbs.Past(activity.Kind, activity.ToolDisplay);
        if (_settledActivityIds.Contains(activity.ActivityId))
        {
            UpdateSettledTool(activity);
            return;
        }
        var index = _pendingTools.FindIndex(tool => tool.ActivityId == activity.ActivityId);

        switch (activity.State)
        {
            case AgentActivityState.Started:
                if (index < 0) _pendingTools.Add(new ToolDetail(
                    activity.ActivityId,
                    activity.State,
                    verb,
                    TuiToolVerbs.Present(activity.Kind, activity.ToolDisplay),
                    activity.ToolDisplay,
                    BuildTarget(activity),
                    activity.Count,
                    activity.Added,
                    activity.Removed,
                    activity.DurationMs));
                TrimPending();
                _status.SetState(TuiRunState.Tool);
                _status.SetActivity(TuiToolVerbs.Present(activity.Kind, activity.ToolDisplay), BuildTarget(activity));
                return;

            case AgentActivityState.Completed:
            case AgentActivityState.Failed:
                if (index >= 0)
                {
                    var last = _pendingTools[index];
                    _pendingTools[index] = last with
                    {
                        State = activity.State,
                        Target = BuildTarget(activity) ?? last.Target,
                        Count = activity.Count ?? last.Count,
                        Added = activity.Added ?? last.Added,
                        Removed = activity.Removed ?? last.Removed,
                        DurationMs = activity.DurationMs ?? last.DurationMs,
                    };
                }
                else
                {
                    _pendingTools.Add(new ToolDetail(
                        activity.ActivityId,
                        activity.State,
                        verb,
                        TuiToolVerbs.Present(activity.Kind, activity.ToolDisplay),
                        activity.ToolDisplay,
                        BuildTarget(activity),
                        activity.Count,
                        activity.Added,
                        activity.Removed,
                        activity.DurationMs));
                    TrimPending();
                }

                return;

            default:
                return;
        }
    }

    private void UpdateSettledTool(AgentActivityModel activity)
    {
        if (!_fullScreenCards || _surface.Writer is not ITranscriptActivityWriter screen
            || activity.State is not (AgentActivityState.Completed or AgentActivityState.Failed)
            || !_settledToolRows.TryGetValue(activity.ActivityId, out var previous)) return;
        var detail = previous with
        {
            State = activity.State,
            Target = BuildTarget(activity) ?? previous.Target,
            Count = activity.Count ?? previous.Count,
            Added = activity.Added ?? previous.Added,
            Removed = activity.Removed ?? previous.Removed,
            DurationMs = activity.DurationMs ?? previous.DurationMs,
        };
        _settledToolRows[activity.ActivityId] = detail;
        var status = activity.State == AgentActivityState.Completed ? ToolLineStatus.Ok : ToolLineStatus.Failed;
        if (_surface.Presentation is { } presentation)
            presentation.UpdateActivityPresentation(activity.ActivityId, TuiToolRow.ComposePresentation(status,
                detail.Display ?? TitleCase(detail.Verb), detail.Target, ToolRowMetrics(detail), Width));
        else screen.UpdateToolRow(activity.ActivityId, TuiToolRow.Compose(status,
            detail.Display ?? TitleCase(detail.Verb), detail.Target, ToolRowMetrics(detail), Width));
    }

    private void TrimPending()
    {
        while (_pendingTools.Count > 32)
        {
            _pendingTools.RemoveAt(0);
        }
    }

    private static string? BuildTarget(AgentActivityModel activity)
    {
        if (activity.ToolDisplay is "Search" or "Fetch" && !string.IsNullOrWhiteSpace(activity.Detail))
            return ChatDisplayText.Sanitize(activity.Detail, 160);
        if (activity.Kind == AgentActivityKind.Terminal && !string.IsNullOrWhiteSpace(activity.Detail))
            return TuiText.Truncate(Escape(activity.Detail), 160);
        if (!string.IsNullOrWhiteSpace(activity.FileName))
        {
            return activity.LineStart is { } start && activity.LineEnd is { } end && end >= start
                ? $"{activity.FileName}:{start}-{end}"
                : activity.FileName;
        }

        if (!string.IsNullOrWhiteSpace(activity.Symbol))
        {
            return activity.Symbol;
        }

        if (activity.Count is { } count)
        {
            return count == 1 ? "1 result" : $"{count} results";
        }

        return null;
    }

    private static string RetryTarget(AgentActivityModel activity) =>
        activity.FileName is { Length: > 0 } fileName
            ? fileName
            : activity.Symbol is { Length: > 0 } symbol
                ? symbol
                : "tool";

    // ------------------------------------------------------------------
    // Durable events: the authoritative record.
    // ------------------------------------------------------------------

    public void ApplyDurable(HarnessSafeEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EventType.StartsWith("run.", StringComparison.Ordinal)
            && envelope.Status is "completed" or "failed" or "cancelled" or "interrupted")
            ForgetParkHistory(envelope.RunId);
        var type = envelope.EventType;
        var summary = Escape(envelope.SafeSummary);

        if (type == "run.state_changed")
        {
            if (string.Equals(envelope.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _sawCompletion = true;
                ClearPaused();
            }
            else if (IsParkedStatus(envelope.Status))
            {
                ParkRun(envelope);
            }
            else if (string.Equals(envelope.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                // The run resumed (approval resolved, reconciliation applied): a pause
                // recorded earlier in this turn no longer holds.
                ClearPaused();
            }

            return;
        }

        if (type == "continue.requested")
        {
            if (IsParkedStatus(envelope.Status))
            {
                ParkRun(envelope);
            }
            else if (string.Equals(envelope.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                // The "continued" commit flips the run back to Running: the parked row
                // must clear, and the frame stays silent like every other internal event.
                ClearPaused();
            }

            return;
        }

        if (type == "model.step_completed"
            && envelope.SafeSummary?.EndsWith("_advised", StringComparison.Ordinal) == true)
        {
            // The host suppressed this step's reply: a recovery note replaced it in the
            // model context and the run takes another step. The deltas already painted
            // it, but suppressed prose is audit-only by design — the rows this step
            // streamed leave the screen so the model's re-answer cannot read as the same
            // answer printed twice.
            _surface.RewindAssistantStep();
            DiscardAssistantTail();
            return;
        }

        if (type is "model.step_started" or "model.step_completed"
            or "prompt_receipt" or "model.usage_recorded" or "model.reasoning_recorded"
            or "tool.proposed" or "input.requested" or "context.compacted")
        {
            // Host bookkeeping, not feed rows. input.requested is answered by the approval
            // path (which prints its own notice) and context.compacted is already surfaced
            // by the compaction activity; printing the durable envelope too duplicated both.
            return;
        }

        if (type == "run.note_added")
        {
            // Internal bookkeeping: runtime-context snapshots, compaction checkpoints,
            // recovery notes and subagent inbox/apply bookkeeping all arrive as run notes.
            // They are audit records, not messages for the operator, and rendering them
            // both added noise and broke the feed block (every notice closes it).
            Tui.TuiDebug.Log("event", $"run.note_added: {summary}");
            return;
        }

        // Metadata can arrive between any two deltas. Only a visible boundary
        // may commit the unfinished Markdown line; flushing above the silent
        // event checks split sentences (and code fences) at network timing.
        Flush();

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

        switch (type)
        {
            case "tool.execution_requested":
                _status.SetState(TuiRunState.Tool);
                return;

            case "tool.execution_completed":
                SettleTool(envelope);
                return;

            case "approval.requested":
                _status.SetState(TuiRunState.WaitingApproval);
                Notice("approval needed · " + summary, TuiToken.Warn);
                return;

            case "reconciliation.required":
                ParkRun(envelope);
                return;

            case "reconciliation.resolved":
                ClearPaused();
                Notice("reconciliation · " + summary, TuiToken.Brand2);
                return;

            case "transient.frames_dropped":
                Notice("some steps were skipped · " + summary, TuiToken.Warn);
                return;

            case "run.failed":
                _terminalCode = string.IsNullOrWhiteSpace(summary) ? "run_failed" : summary;
                return;

            default:
                Notice($"[{envelope.Sequence}] {type}: {summary}", TuiToken.Line);
                return;
        }
    }

    private static bool IsParkedStatus(string? status) =>
        string.Equals(status, "waiting_user", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "reconciliation_required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one parked-run line: the safe code plus both resume paths. Static so the
    /// session-open adoption prints the exact same copy a live park does instead of a
    /// near-duplicate that only looks right.
    /// </summary>
    public static string FormatParkedNotice(string runId, string? summary = null, bool repeated = false)
    {
        var reason = ParkReasonCode(summary);
        if (repeated)
            return $"run parked again · {reason ?? "reason pending"} · {ParkAction(runId, reason ?? string.Empty, true)}";
        return reason is null
            ? $"run parked · resume with Continue (or 'ru agent continue {runId}') · cancel the run from CLI to abandon"
            : $"run parked · {reason} · {ParkAction(runId, reason)}";
    }

    private static string? ParkReasonCode(string? code) =>
        string.IsNullOrWhiteSpace(code) || code is "waiting_user" or "reconciliation_required"
            ? null : code.Trim();

    private static string ParkAction(string runId, string reason, bool repeated = false) =>
        repeated ? "the run is still blocked after Continue · cancel the run from CLI to abandon"
            : reason == "provider_quota_exhausted"
                ? "restore provider credits or limits before Continue · cancel the run from CLI to abandon"
                : reason is "provider_rate_limited" or "provider_timeout" or "provider_server_error" or "provider_transport_error"
                    ? "provider temporarily unavailable · wait before Continue · cancel the run from CLI to abandon"
                    : $"resume with Continue (or 'ru agent continue {runId}') · cancel the run from CLI to abandon";

    /// <summary>
    /// The one line for a run this session already has in flight: the operator can
    /// watch it, but the CLI never attaches on its own.
    /// </summary>
    public static string FormatAttachNotice(string runId) =>
        $"run running · follow it with 'ru agent attach {runId}'";

    /// <summary>
    /// Records a run parked on the operator: the live row stops spinning, carries the
    /// bounded safe code plus the resume path, and the transcript prints exactly one
    /// notice per park. Redundant frames for the same park (the backend commits both
    /// <c>reconciliation.required</c> and <c>run.state_changed</c>) must not repeat it.
    /// </summary>
    private void ParkRun(HarnessSafeEventEnvelope envelope)
    {
        var reason = ParkReasonCode(Escape(envelope.SafeSummary));
        var newPark = !_paused || _pausedRunId != envelope.RunId;
        _parkReasons.TryGetValue(envelope.RunId, out var previousReason);
        if (newPark) { _lastParkReason = null; _repeatedPark = false; }
        if (reason is not null && (newPark || reason != _lastParkReason))
            _repeatedPark = reason == previousReason;
        _paused = true;
        _pausedRunId = envelope.RunId;
        if (newPark)
        {
            _lastParkReason = reason;
            Notice(reason is null && previousReason is not null
                ? $"run parked again · reason pending · cancel the run from CLI to abandon"
                : FormatParkedNotice(envelope.RunId, reason, _repeatedPark), TuiToken.Warn);
        }
        else if (reason is not null && reason != _lastParkReason)
        {
            _lastParkReason = reason;
            Notice($"reason: {reason} · {ParkAction(envelope.RunId, reason, _repeatedPark)}", TuiToken.Warn);
        }
        if (reason is not null)
        {
            _parkReasons[envelope.RunId] = reason;
            while (_parkReasons.Count > 64) _parkReasons.Remove(_parkReasons.Keys.First());
        }
        _status.SetState(TuiRunState.Paused);
        _status.SetDetail(_lastParkReason is null ? "waiting to continue"
            : _lastParkReason + " · " + ParkAction(envelope.RunId, _lastParkReason, _repeatedPark));
    }

    public void ForgetParkHistory(string runId)
    {
        _parkReasons.Remove(runId);
        if (_pausedRunId == runId) ClearPaused();
    }

    private void ClearPaused()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        _pausedRunId = null;
        _lastParkReason = null;
        _status.SetDetail(null);
        _status.SetState(TuiRunState.Thinking);
    }

    private void SettleTool(HarnessSafeEventEnvelope envelope)
    {
        ToolDetail? detail = null;
        // Durable results keep proposal order; completion metadata updates that
        // proposal by activity ID, even when transient completions arrive out of order.
        if (_pendingTools.Count > 0)
        {
            detail = _pendingTools[0];
            _pendingTools.RemoveAt(0);
            _settledActivityIds.Enqueue(detail.ActivityId);
            while (_settledActivityIds.Count > 64) _settledActivityIds.Dequeue();
        }

        // The public envelope's status describes the RUN, not this tool's
        // outcome. A running/completed run must not paint every tool as failed
        // (or every failed tool as successful). Typed activity carries the result.
        var status = envelope.Status.ToLowerInvariant() switch
        {
            "succeeded" => ToolLineStatus.Ok,
            _ when detail?.State == AgentActivityState.Completed => ToolLineStatus.Ok,
            _ when detail?.State == AgentActivityState.Failed => ToolLineStatus.Failed,
            _ => ToolLineStatus.Unknown,
        };
        var failed = status == ToolLineStatus.Failed;

        var target = detail?.Target;
        var resultLine = ResultLine(status, envelope.SafeSummary);

        // A dropped transient frame leaves no target: the bounded safe code is the only
        // auditable detail the durable stream carries, so it takes the target column.
        if (!failed && string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(envelope.SafeSummary))
        {
            var safe = Escape(envelope.SafeSummary);
            if (_fullScreenCards)
            {
                target = TuiText.Truncate(safe, 60);
            }
            else
            {
                target = safe;
                _retained.Add("    " + target);
            }
        }

        if (_fullScreenCards || _surface.Presentation is not null)
        {
            WriteToolRow(status, detail, target, envelope.SafeSummary);
            if (resultLine is not null)
            {
                WriteResultLine(resultLine);
            }
        }
        else
        {
            var display = string.IsNullOrWhiteSpace(detail?.Display)
                ? TitleCase(detail?.Verb ?? "tool")
                : detail!.Display!;
            BeginToolRow(status);
            WriteLines(TuiToolRow.Compose(status, display, target,
                detail is null ? null : ToolRowMetrics(detail), Width));
            if (resultLine is not null)
            {
                WriteResultLine(resultLine);
            }
            ApplyDiffText(envelope.SafeSummary, Width);
        }

        _toolsSettled++;
        _status.AddTool();
        var pending = _pendingTools.FirstOrDefault(tool => tool.State is AgentActivityState.Started or AgentActivityState.Waiting);
        if (pending is null) _status.SetActivity(null);
        else _status.SetActivity(pending.LiveVerb, pending.Target);
        _status.SetState(pending is null ? TuiRunState.Thinking : TuiRunState.Tool);
    }

    /// <summary>
    /// The full-screen settled tool result, OpenCode style: one inline row with the display
    /// name, target and bounded metrics; the diff hunks (no 12-line cap) are retained behind
    /// <c>ctrl+o</c>, reusing the existing expansion key handling.
    /// </summary>
    private void WriteToolRow(
        ToolLineStatus status, ToolDetail? detail, string? target, string? diffText)
    {
        BeginToolRow(status);
        var display = string.IsNullOrWhiteSpace(detail?.Display)
            ? TitleCase(detail?.Verb ?? "tool")
            : detail!.Display!;
        var meta = ComposeToolRowMeta(detail?.Count, detail?.Added, detail?.Removed);
        if (detail?.DurationMs is > 0)
            meta = string.IsNullOrWhiteSpace(meta) ? FormatDuration(detail.DurationMs.Value)
                : meta + " · " + FormatDuration(detail.DurationMs.Value);

        // The count-derived target ("3 results") would duplicate the "(3 matches)" meta.
        if (detail?.Count is not null
            && target is not null
            && (target.EndsWith(" result", StringComparison.Ordinal)
                || target.EndsWith(" results", StringComparison.Ordinal)))
        {
            target = null;
        }

        var diffLines = ExtractDiffLines(diffText);
        if (diffLines.Count > 0)
        {
            meta = string.IsNullOrWhiteSpace(meta) ? "· ctrl+o" : meta + " · ctrl+o";
        }

        if (diffLines.Count > 0)
        {
            // One affordance owns one block: without this, a second diff-bearing tool
            // appended its rows to the previous tool's block and ctrl+o showed a mixture.
            _retained.Clear();
            _retainedIsThinking = false;
            // The expanded diff keeps its tinted rows between two dim rules, so the
            // block reads as one framed unit instead of loose lines.
            var rule = "    " + RetainedPaint(new string('─', Math.Max(4, Width - 4)), TuiToken.Line);
            _retained.Add(rule);
            foreach (var line in diffLines)
            {
                _retained.Add("    " + RetainedPaint("│ ", TuiToken.Line)
                    + (_surface.Presentation is not null ? TuiText.SafeCut(line, 16_000) : FormatDiffLine(line)));
            }

            _retained.Add(rule);
        }

        TrimRetained();
        if (_surface.Presentation is not null)
        {
            WriteStructuredTool(detail?.ActivityId, status, display, target, meta);
            if (detail is not null)
            {
                _settledToolRows[detail.ActivityId] = detail;
                while (_settledToolRows.Count > 64) _settledToolRows.Remove(_settledToolRows.Keys.First());
            }
            return;
        }
        var row = TuiToolRow.Compose(status, display, target, meta, Width);
        if (detail is not null && _surface.Writer is ITranscriptActivityWriter)
        {
            _surface.WriteActivityLine(detail.ActivityId, row);
            _settledToolRows[detail.ActivityId] = detail;
            while (_settledToolRows.Count > 64) _settledToolRows.Remove(_settledToolRows.Keys.First());
        }
        else WriteLines(row);
    }

    private static string? ToolRowMetrics(ToolDetail detail)
    {
        var meta = ComposeToolRowMeta(detail.Count, detail.Added, detail.Removed, detail.Display ?? detail.Verb);
        if (detail.DurationMs is > 0)
            meta = string.IsNullOrWhiteSpace(meta) ? FormatDuration(detail.DurationMs.Value)
                : meta + " · " + FormatDuration(detail.DurationMs.Value);
        return meta;
    }

    /// <summary>
    /// Metrics suffix for an inline tool row: counts and diff stats, no duration. The unit
    /// follows the tool (a read reports lines, a search matches); without a display name the
    /// historical "matches" wording is kept so the API stays compatible with older callers.
    /// </summary>
    public static string? ComposeToolRowMeta(int? count, int? added, int? removed, string? display = null)
    {
        var parts = new List<string>();
        if (count is { } value)
        {
            parts.Add($"{value} {UnitFor(display, value)}");
        }

        if (added is > 0 || removed is > 0)
        {
            parts.Add($"+{added ?? 0} −{removed ?? 0}");
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    private static string UnitFor(string? display, int count)
    {
        var name = display?.Trim().ToLowerInvariant();
        var (singular, plural) = name switch
        {
            null => ("match", "matches"),
            "read" or "view" or "read_file" or "cat" or "open" => ("line", "lines"),
            "edit" or "edit_file" or "apply_patch" or "write" or "write_file" => ("line", "lines"),
            "grep" or "search" or "web_search" or "find" => ("match", "matches"),
            "glob" or "list" or "ls" or "explore" => ("file", "files"),
            "terminal" or "bash" or "run" or "shell" => ("line", "lines"),
            _ => ("item", "items"),
        };
        return count == 1 ? singular : plural;
    }

    private static string TitleCase(string value) =>
        string.IsNullOrEmpty(value) ? "Tool" : char.ToUpperInvariant(value[0]) + value[1..];

    private static List<string> ExtractDiffLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : TuiText.SanitizeUntrusted(text).Split('\n').Where(IsDiffLine).ToList();



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
        _retainedIsThinking = false;
        foreach (var line in flat.Split('\n'))
        {
            var bounded = TuiText.SafeCut(line, 16_000);
            _retained.Add("    " + (_surface.Presentation is not null ? bounded : TuiPalette.DimPaint(bounded)));
        }

        _retainedIsThinking = _retained.Count > 0;
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

    /// <summary>
    /// Opens or extends the current feed block. A block is a run of consecutive settled tool
    /// rows; prose, a notice, or the end of the turn closes it.
    /// </summary>
    private void BeginToolRow(ToolLineStatus status)
    {
        if (!_toolGroupOpen)
        {
            _toolGroupOpen = true;
            _toolGroupCount = 0;
            _toolGroupStarted = _turnWatch.Elapsed.TotalSeconds;
            _toolGroupFailed = false;
        }

        _toolGroupCount++;
        if (status == ToolLineStatus.Failed)
        {
            _toolGroupFailed = true;
        }
    }

    /// <summary>
    /// Closes the feed block and, for more than one row, writes its summary line. The summary
    /// trails the block because a terminal cannot insert a header above rows it already
    /// printed: both the screen and the redirected sink append.
    /// </summary>
    private void CloseToolGroup()
    {
        if (!_toolGroupOpen)
        {
            return;
        }

        var count = _toolGroupCount;
        var failed = _toolGroupFailed;
        var elapsed = Math.Max(0, _turnWatch.Elapsed.TotalSeconds - _toolGroupStarted);
        _toolGroupOpen = false;
        _toolGroupCount = 0;
        _toolGroupFailed = false;
        if (count < 2)
        {
            return;
        }

        var mark = failed ? "✗ " : string.Empty;
        WriteMetaLine($"  {mark}{count} tools · {TuiStatusLine.FormatElapsed(elapsed)}", TuiToken.Meta);
    }

    /// <summary>
    /// The one-line reason under a failed tool. It comes from the durable event's safe
    /// summary (for example <c>exit_1</c>), which is bounded and never contains output;
    /// successful tools have no such line because their summary is a policy class, not a
    /// result.
    /// </summary>
    private static string? ResultLine(ToolLineStatus status, string? safeSummary)
    {
        if (status != ToolLineStatus.Failed || string.IsNullOrWhiteSpace(safeSummary))
        {
            return null;
        }

        var text = ChatDisplayText.Sanitize(safeSummary.Replace('_', ' '), 80).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return text.ToLowerInvariant() is "failed" or "error" or "tool failed" or "succeeded" or "ok"
            ? null
            : text;
    }

    private void WriteResultLine(string text)
    {
        if (_surface.Presentation is not null)
        {
            _surface.AppendPresentation(new TranscriptRunBuilder()
                .Append("    └ ", TuiToken.Line)
                .Append(text, TuiToken.Meta)
                .Line(TranscriptRole.Meta));
        }
        else
        {
            WriteLines(TuiPalette.Paint("    └ ", TuiToken.Line) + TuiPalette.Paint(text, TuiToken.Meta));
        }
    }

    private void WriteMetaLine(string text, TuiToken token)
    {
        if (_surface.Presentation is not null)
        {
            _surface.AppendPresentation(TranscriptLine.Plain(text, TranscriptRole.Meta, token));
        }
        else
        {
            WriteLines(TuiPalette.Paint(text, token));
        }
    }

    // ------------------------------------------------------------------
    // Diffs (bounded) and expansion.
    // ------------------------------------------------------------------

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
            _retainedIsThinking = false;
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
