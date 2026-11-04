using RuApp.Models;

namespace RuApp.Services;

/// <param name="HasActive">Whether <paramref name="Current"/> is in progress rather than the next pending item.</param>
internal sealed record AgentTodoPanelPresentation(bool IsVisible, int Done, int Total, string Current, bool HasActive, double ListHeight)
{
    internal static AgentTodoPanelPresentation Create(IReadOnlyList<TodoItemModel>? todos, double viewportHeight)
    {
        var items = todos ?? Array.Empty<TodoItemModel>();
        int done = items.Count(item => item.IsCompleted);
        var active = items.FirstOrDefault(item => item.IsInProgress);
        string current = active?.Content
            ?? items.FirstOrDefault(item => !item.IsCompleted)?.Content ?? string.Empty;
        return new(items.Count > done, done, items.Count, current, active is not null,
            Math.Clamp(viewportHeight * 0.25, 64, 180));
    }
}
