using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    // What the whole turn did, for its one footer: block summaries used to print a second
    // tally ("✗ 4 edited · 6 failed") right above the footer's own.
    private readonly Dictionary<string, int> _turnWork = [];

    private static string GroupAction(ToolDetail? detail, ToolLineStatus status) => status switch
    {
        ToolLineStatus.Failed => "failed",
        ToolLineStatus.Unknown => "outcome unavailable",
        _ => detail?.Kind switch
        {
            AgentActivityKind.Explore => "read",
            AgentActivityKind.Search => "searched",
            AgentActivityKind.Edit => "edited",
            AgentActivityKind.Terminal => "ran",
            AgentActivityKind.Web => "browsed",
            AgentActivityKind.Skill => "loaded",
            AgentActivityKind.Todo => "plan updated",
            _ => "completed",
        },
    };

    private void RecordGroupWork(ToolDetail? detail, ToolLineStatus status)
    {
        var action = GroupAction(detail, status);
        _turnWork[action] = _turnWork.GetValueOrDefault(action) + 1;
    }

    /// <summary>The footer's tally: edits and failures, the two a reader acts on.</summary>
    private string? TurnWork()
    {
        var parts = new List<string>();
        if (_turnWork.GetValueOrDefault("edited") is > 0 and var edited) parts.Add(edited == 1 ? "1 edit" : $"{edited} edits");
        if (_turnWork.GetValueOrDefault("failed") is > 0 and var failed) parts.Add($"{failed} failed");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private void UpdateGroupWork(ToolDetail previous, ToolDetail detail)
    {
        var before = GroupAction(previous, ToolStatus(previous));
        var after = GroupAction(detail, ToolStatus(detail));
        if (before == after) return;
        _turnWork[before] = Math.Max(0, _turnWork.GetValueOrDefault(before) - 1);
        _turnWork[after] = _turnWork.GetValueOrDefault(after) + 1;
    }
}
