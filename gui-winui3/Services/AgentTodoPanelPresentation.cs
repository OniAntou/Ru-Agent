using RuApp.Models;

namespace RuApp.Services;

internal sealed record AgentTodoPanelPresentation(bool IsVisible, int Done, int Total, string Current, double ListHeight)
{
    internal static AgentTodoPanelPresentation Create(IReadOnlyList<TodoItemModel>? todos, double viewportHeight)
    {
        var items = todos ?? Array.Empty<TodoItemModel>();
        int done = items.Count(item => item.IsCompleted);
        string current = items.FirstOrDefault(item => item.IsInProgress)?.Content
            ?? items.FirstOrDefault(item => !item.IsCompleted)?.Content ?? string.Empty;
        return new(items.Count > done, done, items.Count, current,
            Math.Clamp(viewportHeight * 0.25, 64, 180));
    }
}
