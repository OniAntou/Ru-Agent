using RuAgent.Client;
using RuApp.Models;

namespace RuApp.Services;

internal readonly record struct AgentActivityFeedUpdate(
    AgentActivityItem Item,
    bool Added,
    bool Changed,
    IReadOnlyList<TodoItemModel>? TodoPanelItems);

/// <summary>Maintains activity identity and arrival order independently of view effects.</summary>
internal sealed class AgentActivityFeedPresenter
{
    private readonly Dictionary<string, AgentActivityItem> _items = new(StringComparer.Ordinal);

    internal AgentActivityFeedUpdate? Apply(AgentActivityModel activity, ICollection<AgentActivityItem> rows)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(activity.ActivityId)) return null;

        if (_items.TryGetValue(activity.ActivityId, out var existing))
        {
            var changed = existing.UpdateFrom(activity);
            return new AgentActivityFeedUpdate(existing, Added: false, Changed: changed,
                TodoPanelItems: activity.Todos is null ? null : LatestTodos(rows));
        }

        var item = new AgentActivityItem(activity);
        _items.Add(item.ActivityId, item);
        rows.Add(item);
        return new AgentActivityFeedUpdate(item, Added: true, Changed: true,
            TodoPanelItems: activity.Todos is null ? null : LatestTodos(rows));
    }

    internal void RegisterExisting(AgentActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.TryAdd(item.ActivityId, item);
    }

    internal IReadOnlyList<TodoItemModel>? RegisterReplayedMessages(IEnumerable<ChatMessageItem> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IReadOnlyList<TodoItemModel>? latestTodos = null;
        foreach (var message in messages)
        {
            if (message.AgentActivities is not { } activities) continue;
            foreach (var item in activities)
            {
                RegisterExisting(item);
                if (item.Todos is not null) latestTodos = item.Todos;
            }
        }

        return latestTodos;
    }

    internal void Clear() => _items.Clear();

    private static IReadOnlyList<TodoItemModel>? LatestTodos(IEnumerable<AgentActivityItem> rows) =>
        rows.LastOrDefault(static item => item.Todos is not null)?.Todos;
}
