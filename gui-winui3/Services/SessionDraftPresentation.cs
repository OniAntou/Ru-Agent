using RuApp.Models;

namespace RuApp.Services;

/// <summary>Shows an empty session only while it is open; keeps accepted turns until the server poll catches up.</summary>
internal sealed class SessionDraftPresentation
{
    internal const string PlaceholderTitle = "New Session";
    private sealed record Draft(SessionInfo Row, bool Started);
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
    private string? _activeSessionId;

    internal IReadOnlyList<string> Activate(string? sessionId)
    {
        _activeSessionId = sessionId;
        var discarded = _drafts.Where(pair => !pair.Value.Started && pair.Key != sessionId)
            .Select(pair => pair.Key).ToArray();
        foreach (var id in discarded) _drafts.Remove(id);
        return discarded;
    }

    internal void Register(SessionInfo row, string? workspaceKey, string? workspacePath)
    {
        row.Title = PlaceholderTitle;
        row.WorkspaceId = workspacePath is null ? null : workspaceKey;
        row.WorkspacePath = workspacePath;
        row.WorkspaceLabel = workspacePath is null ? null : WorkspaceSessionProjection.LabelForPath(workspacePath);
        if (string.IsNullOrEmpty(row.CreatedAt)) row.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(row.UpdatedAt)) row.UpdatedAt = row.CreatedAt;
        _drafts[row.Id] = new(row, false);
    }

    internal bool MarkStarted(string sessionId, string prompt)
    {
        if (!_drafts.TryGetValue(sessionId, out var draft) || draft.Started) return false;
        var title = string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > 0) draft.Row.Title = title[..Math.Min(title.Length, 50)];
        _drafts[sessionId] = draft with { Started = true };
        return true;
    }

    internal IReadOnlyList<SessionInfo> Project(IReadOnlyList<SessionInfo> serverRows)
    {
        var durable = serverRows.Where(row => row.MessageCount > 0).ToArray();
        foreach (var row in durable) _drafts.Remove(row.Id);
        // Empty server rows have neither a title nor a workspace yet. Keep the
        // local row until the first durable user message supplies those fields.
        return _drafts.Values
            .Where(draft => draft.Started || draft.Row.Id == _activeSessionId)
            .Select(draft => draft.Row)
            .OrderByDescending(row => row.CreatedAt, StringComparer.Ordinal)
            .Concat(durable).ToArray();
    }

    internal void Remove(string sessionId) => _drafts.Remove(sessionId);
}
