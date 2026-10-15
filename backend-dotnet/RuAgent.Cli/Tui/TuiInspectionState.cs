using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

/// <summary>Bounded, read-only details received by this session's live feed.</summary>
public sealed class TuiInspectionState
{
    private const int MaxActivities = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentActivityModel> _activities = new(StringComparer.Ordinal);
    private IReadOnlyList<AgentTodoWire> _todos = [];

    public static bool Handles(string line) => line.Trim().ToLowerInvariant() is "/activity" or "/todos" or "/changes";

    public void Clear()
    {
        lock (_gate) { _activities.Clear(); _todos = []; }
    }

    public void Observe(AgentActivityModel activity)
    {
        lock (_gate)
        {
            if (activity.Todos is { } todos) _todos = todos.ToArray();
            if (activity.Kind == AgentActivityKind.Todo && string.IsNullOrWhiteSpace(activity.ToolDisplay)) return;
            if (activity.Kind is AgentActivityKind.Narration or AgentActivityKind.Status) return;
            _activities[activity.ActivityId] = activity;
            while (_activities.Count > MaxActivities) _activities.Remove(_activities.Keys.First());
        }
    }

    public IReadOnlyList<string> Rows(string command)
    {
        lock (_gate)
        {
            var rows = command.Trim().ToLowerInvariant() switch
            {
                "/todos" => TodoRows(),
                "/changes" => ChangeRows(),
                _ => ActivityRows(),
            };
            return rows.Count == 0
                ? ["No details received in this connection yet.", "Earlier run details remain available in the GUI."]
                : rows;
        }
    }

    private List<string> TodoRows()
    {
        if (_todos.Count == 0) return [];
        var rows = new List<string> { $"{_todos.Count(todo => todo.Status == "completed")}/{_todos.Count} complete", "" };
        rows.AddRange(_todos.Select(todo =>
            $"{(todo.Status == "completed" ? "✓" : todo.Status == "in_progress" ? "→" : "·")} {Safe(todo.Content)}"));
        return rows;
    }

    private List<string> ChangeRows() => _activities.Values
        .Where(activity => activity.Kind == AgentActivityKind.Edit && activity.State == AgentActivityState.Completed)
        .GroupBy(activity => activity.FileName ?? "file", StringComparer.OrdinalIgnoreCase)
        .Select(group => $"{Safe(group.Key)}  +{group.Sum(item => (long)(item.Added ?? 0))} −{group.Sum(item => (long)(item.Removed ?? 0))}")
        .ToList();

    private List<string> ActivityRows() => _activities.Values.Select(activity =>
    {
        var label = activity.Kind == AgentActivityKind.Thinking ? "Thought" : TuiToolVerbs.Past(activity.Kind, activity.ToolDisplay);
        var target = activity.Kind == AgentActivityKind.Thinking ? null
            : activity.FileName ?? activity.Symbol ?? activity.Detail;
        var state = activity.State switch
        {
            AgentActivityState.Completed => "✓",
            AgentActivityState.Failed => "✗",
            _ => "→",
        };
        var duration = activity.DurationMs is { } milliseconds ? $" · {milliseconds / 1000d:0.0}s" : "";
        return $"{state} {label}{(string.IsNullOrWhiteSpace(target) ? "" : "  " + Safe(target))}{duration}";
    }).ToList();

    private static string Safe(string? text) => ChatDisplayText.Sanitize(text, 800);
}
