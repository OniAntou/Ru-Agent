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
                    OpenThinkingRow(activity);
                }
                else
                {
                    SettleThinkingRow(activity);
                }

                return;

            case AgentActivityKind.Compaction:
                if (activity.State is AgentActivityState.Started or AgentActivityState.Waiting)
                {
                    _status.SetState(_fullScreenCards ? TuiRunState.Compacting : TuiRunState.Thinking);
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

                if (_fullScreenCards) _status.SetState(TuiRunState.WaitingModel);
                return;

            case AgentActivityKind.Retry:
                if (_fullScreenCards)
                {
                    var action = string.IsNullOrWhiteSpace(activity.ToolDisplay) ? "Retrying request" : "Retrying tool";
                    var target = HasToolContext(activity) ? BuildTarget(activity) : null;
                    var attempt = activity.Count is > 0 ? $" · attempt {activity.Count}" : string.Empty;
                    Notice(action.ToLowerInvariant() + (target is null ? string.Empty : " · " + target) + attempt, TuiToken.Warn);
                    _status.SetActivity(action, target);
                    _status.SetState(TuiRunState.Retrying);
                }
                else Notice($"retrying {RetryTarget(activity)}", TuiToken.Warn);
                return;

            case AgentActivityKind.Status:
                if (!string.IsNullOrWhiteSpace(activity.ToolDisplay))
                {
                    ApplyToolActivity(activity);
                    return;
                }
                // Subagent waits are announced by the subagent strip and its transition
                // lines; a notice per wait (and per check-in) only repeated that.
                if (activity.SafeCode is "frames_dropped" or "waiting_subagents"
                    or "subagent_wait_timeout" or "subagent_wait_completed") return;
                // A status row exists to explain something. "ok" explains nothing, and a
                // raw code ("subagent_spawned") is not a sentence; the Desktop feed hides
                // the first and words the second. A failure is never hidden.
                if (TuiActivityLabels.IsSilentStatus(activity.State, activity.SafeCode)) return;
                Notice(TuiActivityLabels.StatusText(activity.SafeCode),
                    activity.State == AgentActivityState.Failed ? TuiToken.Err : TuiToken.Line);
                return;

            case AgentActivityKind.Todo:
                if (activity.Todos is { Count: > 0 } todos) _pendingPlan = todos;
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

    /// <summary>
    /// Opens the step's thinking row before the answer it precedes streams.
    /// </summary>
    /// <remarks>
    /// The Host closes a model step only after the assistant text has already reached the
    /// transcript, so announcing the thought on completion put "Thought 6.1s" under the
    /// reply it introduced. The row opens live and settles in place, the way the Desktop
    /// feed does it. A plain output surface cannot rewrite a row, so it keeps the
    /// completion-only path below.
    /// </remarks>
    private void OpenThinkingRow(AgentActivityModel activity)
    {
        if (_surface.Presentation is null || !_thinkingRows.Add(activity.ActivityId)) return;
        var row = new TranscriptRunBuilder()
            .Append("  ◦ ", TuiToken.Line)
            .Append("Thinking", TuiToken.Meta)
            .Line(TranscriptRole.Meta);
        _surface.AppendActivityPresentation(activity.ActivityId, row, null);
    }

    /// <summary>
    /// Settles the thinking row opened for this step, or announces it here when the
    /// surface never opened one (a plain writer, or a replayed turn that keeps only the
    /// final state).
    /// </summary>
    private void SettleThinkingRow(AgentActivityModel activity)
    {
        var failed = activity.State == AgentActivityState.Failed;
        // Where the screen cannot keep the reasoning for the footer's hint, the row itself
        // says how to open it.
        var hint = !string.IsNullOrWhiteSpace(activity.Detail)
            && !(_fullScreenCards && _surface.Writer is ITranscriptLineWriter) ? " · ctrl+o" : string.Empty;
        var text = failed
            ? "Thought stopped"
            : activity.DurationMs switch
            {
                null => "Thought",
                < 1000 => "Thought briefly",
                var duration => "Thought " + TuiStatusLine.FormatElapsed(duration.Value / 1000d) + hint,
            };

        var opened = _thinkingRows.Remove(activity.ActivityId);
        if (opened)
        {
            var row = new TranscriptRunBuilder()
                .Append("  ◦ ", TuiToken.Line)
                .Append(text, failed ? TuiToken.Err : TuiToken.Meta)
                .Line(TranscriptRole.Meta);
            _surface.Presentation?.UpdateActivityPresentation(activity.ActivityId, row, null);
        }
        else if (!failed && activity.DurationMs is >= 1000)
        {
            Flush();
            MetaRow("◦ ", text, TuiToken.Meta);
        }

        if (!string.IsNullOrWhiteSpace(activity.Detail))
        {
            RetainThinking(activity.Detail, announced: opened || (!failed && activity.DurationMs is >= 1000));
        }
    }

    private void ApplyToolActivity(AgentActivityModel activity)
    {
        if (_settledActivityIds.Contains(activity.ActivityId))
        {
            UpdateSettledTool(activity);
            return;
        }
        var index = _pendingTools.FindIndex(tool => tool.ActivityId == activity.ActivityId);

        switch (activity.State)
        {
            case AgentActivityState.Started:
                if (index < 0) _pendingTools.Add(DescribeTool(activity));
                TrimPending();
                _status.SetState(TuiRunState.Tool);
                _status.SetActivity(TuiToolVerbs.Present(activity.Kind, activity.ToolDisplay), BuildTarget(activity));
                return;

            case AgentActivityState.Completed:
            case AgentActivityState.Failed:
                if (index >= 0)
                {
                    _pendingTools[index] = MergeTool(_pendingTools[index], activity);
                }
                else
                {
                    _pendingTools.Add(DescribeTool(activity));
                    TrimPending();
                }

                return;

            default:
                return;
        }
    }

    private void UpdateSettledTool(AgentActivityModel activity)
    {
        if (!_fullScreenCards || activity.State is not (AgentActivityState.Completed or AgentActivityState.Failed)
            || !_settledToolRows.TryGetValue(activity.ActivityId, out var previous)) return;
        var detail = MergeTool(previous, activity);
        _settledToolRows[activity.ActivityId] = detail;
        var status = ToolStatus(detail);
        if (_surface.Presentation is { } presentation)
        {
            UpdateGroupWork(previous, detail);
            presentation.UpdateActivityPresentation(activity.ActivityId, TuiToolRow.ComposePresentation(status,
                ActionLabel(detail, status), detail.Target, InspectorMetrics(detail), Width,
                actionColumnWidth: ActivityActionColumnWidth), null);
        }
        else if (_surface.Writer is ITranscriptActivityWriter screen) screen.UpdateToolRow(activity.ActivityId, TuiToolRow.Compose(status,
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
        {
            // The row keeps the start and end of a long target, so the remaining
            // statement count survives truncation next to the command it follows.
            var (head, more) = TuiActivityLabels.CommandHead(activity.Detail);
            var shown = TuiText.Truncate(Escape(head), 160);
            return more > 0 ? $"{shown}  (+{more} more)" : shown;
        }
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
        var resultLine = ResultLine(status, detail?.State == AgentActivityState.Failed
            && detail.SafeCode is not ("started" or "succeeded" or "ok" or "failed" or "error")
            ? detail.SafeCode : envelope.SafeSummary);
        // "process exit nonzero" under a row that already says "failed · exit 1" repeats it.
        // A real reason (timeout, denial, invalid plan) still gets its line.
        if (detail?.ExitCode is not null && detail.SafeCode is "process_exit_nonzero" or "process_failed" or "exit_nonzero")
            resultLine = null;

        // A dropped transient frame leaves no target: the bounded safe code is the only
        // auditable detail the durable stream carries, so it takes the target column.
        // A known tool with no target (terminal_read, terminal_stop) keeps an empty column:
        // its summary is a policy class ("read_only"), not something it acted on.
        if (!failed && detail is null && string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(envelope.SafeSummary))
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
            WriteToolRow(status, detail, target);
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
            WriteLines(TuiToolRow.Compose(status, display, target,
                detail is null ? null : ToolRowMetrics(detail), Width));
            if (resultLine is not null)
            {
                WriteResultLine(resultLine);
            }
        }

        // A bounded diff is part of the inline result now that tool rows have no
        // inspector; both surfaces show the same first lines and overflow count.
        ApplyDiffText(envelope.SafeSummary, Width);

        _toolsSettled++;
        RecordGroupWork(detail, status);
        _status.AddTool();
        var pending = _pendingTools.FirstOrDefault(tool => tool.State is AgentActivityState.Started or AgentActivityState.Waiting);
        if (pending is null) _status.SetActivity(null);
        else _status.SetActivity(pending.LiveVerb, pending.Target);
        _status.SetState(pending is null
            ? _fullScreenCards ? TuiRunState.WaitingModel : TuiRunState.Thinking
            : TuiRunState.Tool);
    }

    /// <summary>
    /// The settled tool result: one inline row with the display name, target and bounded
    /// metrics. The row carries no hidden inspector; Ctrl+O belongs to reasoning only.
    /// </summary>
    private void WriteToolRow(ToolLineStatus status, ToolDetail? detail, string? target)
    {
        if (_surface.Presentation is not null)
        {
            WriteNativeTool(detail, status, target);
            return;
        }
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

        TrimRetained();
        var row = TuiToolRow.Compose(status, display, target, meta, Width);
        if (detail is not null && _surface.Writer is ITranscriptActivityWriter)
        {
            _surface.WriteActivityLine(detail.ActivityId, row);
            _settledToolRows[detail.ActivityId] = detail;
        }
        else WriteLines(row);
    }

    private static string? ToolRowMetrics(ToolDetail detail, bool includeExitCode = false)
    {
        var meta = ComposeToolRowMeta(detail.Count, detail.Added, detail.Removed, detail.Display ?? detail.Verb);
        if (detail.DurationMs is > 0)
            meta = string.IsNullOrWhiteSpace(meta) ? FormatDuration(detail.DurationMs.Value)
                : meta + " · " + FormatDuration(detail.DurationMs.Value);
        if (includeExitCode && detail.ExitCode is { } code)
            meta = string.IsNullOrWhiteSpace(meta) ? $"exit {code}" : $"exit {code} · " + meta;
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
