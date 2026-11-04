using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    private readonly Dictionary<string, int> _toolGroupWork = [];
    private int _toolGroupSerial;

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
        _toolGroupWork[action] = _toolGroupWork.GetValueOrDefault(action) + 1;
    }

    private void UpdateGroupWork(ToolDetail previous, ToolDetail detail)
    {
        if (!_toolGroupOpen || previous.Group != _toolGroupSerial) return;
        var before = GroupAction(previous, ToolStatus(previous));
        var after = GroupAction(detail, ToolStatus(detail));
        if (before == after) return;
        _toolGroupWork[before] = Math.Max(0, _toolGroupWork.GetValueOrDefault(before) - 1);
        RecordGroupWork(detail, ToolStatus(detail));
        _toolGroupFailed = _toolGroupWork.GetValueOrDefault("failed") > 0;
    }
}
