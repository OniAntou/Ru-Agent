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

        var now = Environment.TickCount64;
        if (_items.TryGetValue(activity.ActivityId, out var existing))
        {
            var wasRunning = existing.IsRunning;
            var changed = existing.UpdateFrom(activity);
            if (changed) existing.ObserveLiveFrame(created: false, wasRunning, now);
            return new AgentActivityFeedUpdate(existing, Added: false, Changed: changed,
                TodoPanelItems: activity.Todos is null ? null : LatestTodos(rows));
        }

        var item = new AgentActivityItem(activity);
        // Only this path sees a row happen live; replay and adoption never call it.
        item.ObserveLiveFrame(created: true, wasRunning: false, now);
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

    /// <summary>
    /// Routes one absolute thinking-preview line to the row it names. An id this feed has
    /// never seen is dropped instead of creating a row: the activity frame owns arrival.
    /// </summary>
    /// <returns>True when the row exists and absorbed the line.</returns>
    internal bool ApplyThinkingPreview(string activityId, string text)
    {
        if (string.IsNullOrEmpty(activityId) || !_items.TryGetValue(activityId, out var item))
        {
            return false;
        }

        item.SetThinkingPreview(text);
        return true;
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
