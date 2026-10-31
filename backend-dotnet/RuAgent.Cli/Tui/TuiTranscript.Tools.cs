using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
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
            case AgentActivityKind.Web:
            case AgentActivityKind.Mcp:
            case AgentActivityKind.Computer:
            case AgentActivityKind.Skill:
            case AgentActivityKind.Program:
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
        if ((activity.ToolDisplay is "Search" or "Fetch"
                || activity.Kind is AgentActivityKind.Web or AgentActivityKind.Mcp
                    or AgentActivityKind.Computer or AgentActivityKind.Skill)
            && !string.IsNullOrWhiteSpace(activity.Detail))
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
}
