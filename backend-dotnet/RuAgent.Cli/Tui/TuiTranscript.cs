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
}

/// <summary>
/// Turns the event stream into finalised transcript lines. Outcome truth is the durable
/// event stream; the transient activity frames only supply detail (verb, target, counts,
/// duration), so a dropped transient frame degrades a line instead of losing it.
/// </summary>
public sealed class TuiTranscript
{
    private const int MaxDiffLines = 12;
    private const int MaxRetainedLines = 200;
    private const int VerbColumn = 8;
    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    /// <summary>
    /// Cap on the raw assistant text retained per turn for gap detection against the run's
    /// durable assistant payload. Matches the worker's own per-step text cap.
    /// </summary>
    private const int MaxRetainedAssistantCharacters = 1_000_000;

    private readonly TuiSurface _surface;
    private readonly TuiStatusLine _status;
    private readonly TuiMarkdown _markdown = new();
    private readonly List<ToolDetail> _pendingTools = [];
    private readonly List<string> _retained = [];
    private readonly StringBuilder _streamedAssistant = new();
    private readonly Stopwatch _turnWatch = new();

    private bool _hasStreamedDelta;
    private string _lineBuffer = string.Empty;
    private bool _inToolBlock;
    private bool _atLineStart = true;
    private bool _proseContinuesWord;
    private int _toolsSettled;
    private string? _terminalCode;
    private string? _summaryDetail;
    private bool _sawCompletion;

    /// <summary>
    /// The raw assistant text this turn actually received as deltas, before markdown
    /// rendering. Deltas published before the client subscribes are gone for good -- the
    /// live hub has no replay buffer -- so this is what a gap check compares the run's
    /// durable assistant payload against.
    /// </summary>
    public string StreamedAssistantText => _streamedAssistant.ToString();

    /// <summary>
    /// The rows a rebuilt screen would print right now. Exposed so a repaint can be asserted
    /// on the surface's recorded lines rather than on escape sequences.
    /// </summary>
    public IReadOnlyList<string> RepaintPreview(int maxLines = 200) =>
        _surface.SnapshotTail(maxLines, Width);

    /// <summary>Recorded-line index where this turn's assistant output began, or -1.</summary>
    private int _assistantBlockStartRow = -1;

    /// <summary>
    /// Rewrites this turn's assistant block with the run's complete text.
    ///
    /// A terminal cannot unprint, so a reply whose head never arrived cannot be repaired by
    /// appending: the head would land after the tail and the answer would read as garbage.
    /// The block is instead replaced in the surface's recorded history and the caller
    /// rebuilds the screen from it, which is the same path a resize already uses.
    ///
    /// Returns false when nothing was streamed this turn, or when the rows have already
    /// scrolled out of the rebuildable window -- in both cases the answer is not lost but
    /// cannot be placed correctly, and the caller should say so rather than guess.
    /// </summary>
    public bool RepaintAssistantText(string fullText)
    {
        if (string.IsNullOrEmpty(fullText) || _assistantBlockStartRow < 0)
        {
            return false;
        }

        var width = Width;
        var rendered = new List<string>();
        var markdown = new TuiMarkdown();
        foreach (var raw in fullText.Replace("\r", string.Empty).Split('\n'))
        {
            rendered.AddRange(markdown.RenderLine(raw, width));
        }

        if (rendered.Count == 0)
        {
            return false;
        }

        // The in-progress line has not been recorded yet, so it is not part of the block.
        var recorded = _surface.HistoryCount - _assistantBlockStartRow;
        if (recorded <= 0)
        {
            return false;
        }

        if (!_surface.ReplaceRecordedTail(recorded, rendered))
        {
            return false;
        }

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

    /// <summary>
    /// Writes finished lines and repaints the live row in the same lock acquisition, so the
    /// spinner can never interleave between two transcript lines.
    /// </summary>
    private void WriteLines(params string[] lines) =>
        _surface.WriteBlock(lines, _status.LiveRow(Width));

    private sealed record ToolDetail(string Verb, string? Target, int? Added, int? Removed, int? DurationMs, bool Failed);

    public void BeginTurn()
    {
        _toolsSettled = 0;
        _hasStreamedDelta = false;
        _lineBuffer = string.Empty;
        _inToolBlock = false;
        _atLineStart = true;
        _proseContinuesWord = false;
        _terminalCode = null;
        _summaryDetail = null;
        _sawCompletion = false;
        _pendingTools.Clear();
        _retained.Clear();
        _markdown.Reset();
        _turnWatch.Restart();
        _streamedAssistant.Clear();
        _assistantBlockStartRow = -1;

        _surface.Line();
    }

    // ------------------------------------------------------------------
    // Streaming assistant text (markers swallowed, block text guttered).
    // ------------------------------------------------------------------

    public void AppendAssistant(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_assistantBlockStartRow < 0)
        {
            _assistantBlockStartRow = _surface.HistoryCount;
        }

        if (_streamedAssistant.Length < MaxRetainedAssistantCharacters)
        {
            _streamedAssistant.Append(text);
        }

        if (!_hasStreamedDelta)
        {
            _hasStreamedDelta = true;
            _status.Clear(Width);
            if (_toolsSettled > 0 && _atLineStart)
            {
                _surface.Line();
            }
            WriteLines(ComposeAssistantHeader(DateTime.Now));
            _atLineStart = false;
        }

        _lineBuffer += text;
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

    /// <summary>One assistant header per turn: glyph, role and time, printed before the first delta.</summary>
    public static string ComposeAssistantHeader(DateTime moment) =>
        TuiPalette.Paint("◆ ", TuiToken.Brand)
        + TuiPalette.BoldPaint("Ru")
        + TuiPalette.Paint(" · " + moment.ToString("HH:mm"), TuiToken.Meta);

    /// <summary>
    /// Drops text held back for the current streamed answer. A retried model step
    /// replaces the attempt that failed, so the pending buffer must not leak into
    /// the new attempt's text. Text already flushed to the surface cannot be
    /// un-written in a terminal; it is superseded by the retry's output.
    /// </summary>
    public void ResetAssistant()
    {
        // A superseded attempt's answer is replaced by the retry: drop its pending
        // render buffer, and its raw text from gap tracking, so the retry -- not the
        // stale head it replaced -- is what gets compared against the durable payload.
        // Already rendered rows cannot be un-written; the retry supersedes them visually.
        // The turn's block marker is intentionally kept so a later repaint can still
        // target the whole turn, and the header is not reprinted.
        _lineBuffer = string.Empty;
        _streamedAssistant.Clear();
        _markdown.Reset();
        _proseContinuesWord = false;
    }

    public void Flush()
    {
        if (_lineBuffer.Length == 0)
        {
            return;
        }

        var rest = _lineBuffer;
        _lineBuffer = string.Empty;
        RenderStyled(rest);
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
                _surface.Line();
                _atLineStart = true;
            }

            // Prose on both sides of a tool block is independent text: a fence open when the
            // tool call started cannot survive it.
            _markdown.Reset();
            _proseContinuesWord = false;
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
                if (_atLineStart)
                {
                    _surface.Write(TuiPalette.Paint("  │ ", TuiToken.Line));
                }

                _surface.Write(TuiPalette.DimPaint(line));
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
    /// are decided; the unfinished tail of a line is rendered inline, which is what keeps
    /// a streamed answer visible while its last line is still arriving.
    /// </summary>
    private void WriteProse(string text)
    {
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                if (start < text.Length)
                {
                    WriteInlineFragment(text[start..]);
                    _atLineStart = false;
                }

                return;
            }

            WriteCompleteLine(text[start..newline]);
            start = newline + 1;
            if (start >= text.Length)
            {
                _atLineStart = true;
                return;
            }
        }
    }

    private void WriteInlineFragment(string fragment)
    {
        var rendered = TuiMarkdown.RenderInline(fragment, Width, _proseContinuesWord);
        _proseContinuesWord = EndsWithWordCharacter(rendered);
        _surface.Write(rendered);
    }

    private void WriteCompleteLine(string line)
    {
        foreach (var row in _markdown.RenderLine(line, Width))
        {
            _surface.Write(row);
            _surface.Line();
        }

        if (_markdown.InFence)
        {
            return;
        }

        _proseContinuesWord = false;
    }

    private static bool EndsWithWordCharacter(string rendered)
    {
        var last = rendered.Length == 0 ? '\0' : rendered[^1];
        return char.IsLetterOrDigit(last);
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
                if (activity.SafeCode != "frames_dropped")
                {
                    Notice(activity.SafeCode, TuiToken.Line);
                }

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

        switch (activity.State)
        {
            case AgentActivityState.Started:
                _pendingTools.Add(new ToolDetail(
                    verb,
                    BuildTarget(activity),
                    activity.Added,
                    activity.Removed,
                    activity.DurationMs,
                    Failed: false));
                TrimPending();
                _status.SetState(TuiRunState.Tool);
                _status.SetActivity(BuildActivityLabel(TuiToolVerbs.Present(activity.Kind, activity.ToolDisplay), activity));
                return;

            case AgentActivityState.Completed:
            case AgentActivityState.Failed:
                if (_pendingTools.Count > 0)
                {
                    var last = _pendingTools[^1];
                    _pendingTools[^1] = last with
                    {
                        Added = activity.Added ?? last.Added,
                        Removed = activity.Removed ?? last.Removed,
                        DurationMs = activity.DurationMs ?? last.DurationMs,
                        Failed = activity.State == AgentActivityState.Failed,
                    };
                }
                else
                {
                    _pendingTools.Add(new ToolDetail(
                        verb,
                        BuildTarget(activity),
                        activity.Added,
                        activity.Removed,
                        activity.DurationMs,
                        activity.State == AgentActivityState.Failed));
                }

                return;

            default:
                return;
        }
    }

    private void TrimPending()
    {
        while (_pendingTools.Count > 32)
        {
            _pendingTools.RemoveAt(0);
        }
    }

    private static string BuildActivityLabel(string verb, AgentActivityModel activity)
    {
        var target = BuildTarget(activity);
        return string.IsNullOrWhiteSpace(target) ? verb : verb + " " + target;
    }

    private static string? BuildTarget(AgentActivityModel activity)
    {
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
        Flush();

        var type = envelope.EventType;
        var summary = Escape(envelope.SafeSummary);

        if (type == "run.state_changed")
        {
            if (string.Equals(envelope.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _sawCompletion = true;
            }

            return;
        }

        if (type is "model.step_started" or "model.step_completed"
            or "prompt_receipt" or "model.usage_recorded" or "model.reasoning_recorded"
            or "tool.proposed" or "continue.requested")
        {
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
            case "reconciliation.resolved":
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

    private void SettleTool(HarnessSafeEventEnvelope envelope)
    {
        ToolDetail? detail = null;
        if (_pendingTools.Count > 0)
        {
            detail = _pendingTools[0];
            _pendingTools.RemoveAt(0);
        }

        var failed = !string.Equals(envelope.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(envelope.Status, "completed", StringComparison.OrdinalIgnoreCase);
        failed |= detail?.Failed == true;

        var meta = ComposeToolMeta(detail?.Added, detail?.Removed, detail?.DurationMs);
        var target = detail?.Target;

        // A dropped transient frame leaves no target: the bounded safe code is the only
        // auditable detail the durable stream carries, so it takes the target column.
        if (!failed && string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(envelope.SafeSummary))
        {
            target = Escape(envelope.SafeSummary);
            _retained.Add("    " + target);
        }

        var line = ComposeToolLine(
            failed ? ToolLineStatus.Failed : ToolLineStatus.Ok,
            detail?.Verb ?? "tool",
            target,
            meta,
            Width);

        WriteLines(line);
        ApplyDiffText(envelope.SafeSummary, Width);
        _toolsSettled++;
        _status.AddTool();
        _status.SetActivity(null);
        _status.SetState(TuiRunState.Thinking);
    }

    /// <summary>One settled tool line: glyph, verb column, target, right-aligned meta.</summary>
    public static string ComposeToolLine(
        ToolLineStatus status,
        string verb,
        string? target,
        string? meta,
        int width)
    {
        var (glyph, token) = status switch
        {
            ToolLineStatus.Ok => ("✓", TuiToken.Ok),
            ToolLineStatus.Failed => ("✗", TuiToken.Err),
            _ => ("▸", TuiToken.Brand),
        };

        var head = "  "
            + TuiPalette.Paint("▌", TuiToken.Line)
            + " "
            + TuiPalette.Paint(glyph, token)
            + " "
            + TuiPalette.Paint(verb.PadRight(VerbColumn), TuiToken.Brand2);
        var body = head + (target ?? string.Empty);
        if (string.IsNullOrWhiteSpace(meta))
        {
            return TuiText.Pad(body, width);
        }

        var metaText = TuiPalette.Paint(meta, TuiToken.Meta);
        var headWidth = TuiText.VisibleWidth(head);
        var metaWidth = TuiText.VisibleWidth(metaText);
        var targetRoom = width - headWidth - metaWidth - 2;
        if (targetRoom < 8)
        {
            return TuiText.Pad(body, width);
        }

        var clippedTarget = TuiText.Pad(target ?? string.Empty, targetRoom);
        return head + clippedTarget + "  " + metaText;
    }

    public static string? ComposeToolMeta(int? added, int? removed, int? durationMs)
    {
        var parts = new List<string>();
        if (added is > 0)
        {
            parts.Add("+" + added);
        }

        if (removed is > 0)
        {
            parts.Add("-" + removed);
        }

        if (durationMs is { } duration)
        {
            parts.Add(FormatDuration(duration));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
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

        foreach (var line in flat.Split('\n'))
        {
            _retained.Add("    " + TuiPalette.DimPaint(line));
        }

        TrimRetained();
        Notice("thought · ctrl+o", TuiToken.Line);
    }

    private void Notice(string text, TuiToken token)
    {
        Flush();
        if (!_atLineStart)
        {
            _surface.Line();
            _atLineStart = true;
        }

        WriteLines(TuiPalette.Paint("  · " + Escape(text), token));
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

        var lines = text.Split('\n');
        var diffLines = lines.Where(IsDiffLine).ToList();
        if (diffLines.Count == 0)
        {
            return;
        }

        foreach (var line in diffLines.Take(MaxDiffLines))
        {
            WriteLines("    " + TuiPalette.Paint("│ ", TuiToken.Line) + FormatDiffLine(line));
        }

        if (diffLines.Count > MaxDiffLines)
        {
            _retained.Clear();
            foreach (var line in diffLines)
            {
                _retained.Add("    " + TuiPalette.Paint("│ ", TuiToken.Line) + FormatDiffLine(line));
            }

            TrimRetained();
            WriteLines(TuiPalette.Paint(
                $"    │ · {diffLines.Count - MaxDiffLines} more lines · ctrl+o",
                TuiToken.Line));
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
        _surface.WriteBlock(block, _status.LiveRow(Width));
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

        switch (outcome)
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
                _surface.WriteBlock(
                    ComposeFailure(seconds, errorCode ?? _terminalCode ?? "run_failed"),
                    _status.LiveRow(Width));
                break;
        }

        _toolsSettled = 0;
        _terminalCode = null;
        _summaryDetail = null;
        _pendingTools.Clear();
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
        durationMs < 1000 ? $"{durationMs}ms" : $"{durationMs / 1000d:0.#}s";

    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length > 500)
        {
            text = text[..500] + "…";
        }

        return text.Replace("\r", " ").Replace("\n", " ");
    }
}
