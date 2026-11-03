using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    private const int ActivityActionColumnWidth = TuiActivityPresentation.ActionColumnWidth;
    private const int MaxInspectorDiffLineCharacters = 16_000;
    private sealed record ToolDetail(
        string ActivityId, AgentActivityState State, string Verb, string LiveVerb,
        string? Display, string? Target, int? Count, int? Added, int? Removed, int? DurationMs,
        AgentActivityKind Kind, int? ExitCode, string SafeCode, string? Context,
        IReadOnlyList<string>? Diff = null, int Group = 0);

    private static ToolDetail DescribeTool(AgentActivityModel activity) =>
        new(activity.ActivityId, activity.State, TuiToolVerbs.Past(activity.Kind, activity.ToolDisplay),
            TuiToolVerbs.Present(activity.Kind, activity.ToolDisplay), activity.ToolDisplay,
            BuildTarget(activity), activity.Count, activity.Added, activity.Removed, activity.DurationMs,
            activity.Kind, activity.ExitCode, ChatDisplayText.Sanitize(activity.SafeCode, 120),
            ToolContext(activity));

    private static ToolDetail MergeTool(ToolDetail previous, AgentActivityModel activity) => previous with
    {
        State = activity.State,
        Target = HasToolContext(activity) ? BuildTarget(activity) ?? previous.Target : previous.Target ?? BuildTarget(activity),
        Count = activity.Count ?? previous.Count,
        Added = activity.Added ?? previous.Added,
        Removed = activity.Removed ?? previous.Removed,
        DurationMs = activity.DurationMs ?? previous.DurationMs,
        ExitCode = activity.ExitCode ?? previous.ExitCode,
        SafeCode = ChatDisplayText.Sanitize(activity.SafeCode, 120),
        Context = HasToolContext(activity) ? ToolContext(activity) ?? previous.Context : previous.Context ?? ToolContext(activity),
    };

    private static bool HasToolContext(AgentActivityModel activity) =>
        !string.IsNullOrWhiteSpace(activity.Detail) || !string.IsNullOrWhiteSpace(activity.FileName)
        || !string.IsNullOrWhiteSpace(activity.Symbol);

    private static string? ToolContext(AgentActivityModel activity)
    {
        var context = string.IsNullOrWhiteSpace(activity.Detail) ? BuildTarget(activity) : activity.Detail;
        return string.IsNullOrWhiteSpace(context) ? null : ChatDisplayText.Sanitize(context, 1024);
    }

    private static string ActionLabel(ToolDetail? detail, ToolLineStatus status) =>
        status == ToolLineStatus.Ok && detail is not null ? TitleCase(detail.Verb)
            : string.IsNullOrWhiteSpace(detail?.Display) ? "Tool" : detail.Display;

    private static ToolLineStatus ToolStatus(ToolDetail detail) => detail.State switch
    {
        AgentActivityState.Completed => ToolLineStatus.Ok,
        AgentActivityState.Failed => ToolLineStatus.Failed,
        _ => ToolLineStatus.Unknown,
    };

    private static IReadOnlyList<string> InspectTool(ToolDetail detail, ToolLineStatus status)
    {
        var rows = new List<string>
        {
            "    Tool     " + ChatDisplayText.Sanitize(detail.Display ?? detail.Verb, 64),
            "    Outcome  " + (status switch
            {
                ToolLineStatus.Ok => "Completed",
                ToolLineStatus.Failed => "Failed",
                _ => "Outcome unavailable",
            }),
        };
        if (!string.IsNullOrWhiteSpace(detail.Context))
            rows.Add((detail.Kind == AgentActivityKind.Terminal ? "    Command  " : "    Target   ") + detail.Context);
        var metrics = ToolRowMetrics(detail, includeExitCode: true);
        if (!string.IsNullOrWhiteSpace(metrics)) rows.Add("    Result   " + metrics);
        if (status == ToolLineStatus.Failed && !string.IsNullOrWhiteSpace(detail.SafeCode))
            rows.Add("    Code     " + detail.SafeCode);
        if (detail.Diff is { Count: > 0 })
        {
            rows.Add("    Diff");
            rows.AddRange(detail.Diff.Take(MaxRetainedLines - rows.Count)
                .Select(line => "    " + TuiText.SafeCut(TuiText.SanitizeUntrusted(line), MaxInspectorDiffLineCharacters - 4)));
        }
        return rows;
    }

    private static string InspectorMetrics(ToolDetail? detail)
    {
        if (detail is null) return string.Empty;
        var metrics = ToolRowMetrics(detail, includeExitCode: true);
        return string.IsNullOrWhiteSpace(metrics) ? "ctrl+o details" : metrics + " · ctrl+o details";
    }

    private void WriteNativeTool(ToolDetail? detail, ToolLineStatus status, string? target, string? diffText)
    {
        // Ctrl+O now belongs to this activity's inspector; older reasoning remains
        // available in its own block, without a misleading hint on the turn footer.
        _retainedIsThinking = false;
        if (detail is not null)
            detail = detail with { Diff = ExtractDiffLines(diffText).Take(MaxRetainedLines).ToArray(), Group = _toolGroupSerial };
        var row = TuiToolRow.ComposePresentation(status, ActionLabel(detail, status), target, InspectorMetrics(detail), Width,
            actionColumnWidth: ActivityActionColumnWidth);
        if (detail is null) _surface.AppendPresentation(row);
        else
        {
            _surface.AppendActivityPresentation(detail.ActivityId, row, InspectTool(detail, status));
            _settledToolRows[detail.ActivityId] = detail;
        }
        RecordGroupWork(detail, status);
    }
}
