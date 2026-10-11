using RuApp.Models;

namespace RuApp.Services;

/// <summary>Sidebar placement never rewrites a session's execution workspace.</summary>
internal static class SidebarSessionOrganization
{
    internal const string RecentsKey = "recents";
    internal const string WorkspacesKey = "workspaces";

    internal static bool SamePath(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
        && string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
    internal static string PathKey(string path) => "path:" + Normalize(path).ToLowerInvariant();

    internal static WorkspaceGroupIdentity GroupFor(SessionInfo session, string? appRoot, SidebarOrganizationPreferences preferences)
    {
        var path = preferences.Groups.TryGetValue(session.Id, out var placement) ? placement : session.WorkspacePath;
        if (string.IsNullOrEmpty(path) || SamePath(path, appRoot)) return new(RecentsKey, "Recents", null);
        return new(PathKey(path), WorkspaceSessionProjection.LabelForPath(path), path);
    }

    internal static IReadOnlyList<SessionInfo> Order(IReadOnlyList<SessionInfo> sessions, SidebarOrganizationPreferences preferences)
    {
        var ranks = preferences.Order.Select((id, rank) => (id, rank))
            .ToDictionary(pair => pair.id, pair => pair.rank, StringComparer.Ordinal);
        // The active New Session remains visible ahead of manually ordered history.
        return sessions.OrderBy(row => row.MessageCount > 0 ? 1 : 0)
            .ThenBy(row => ranks.GetValueOrDefault(row.Id, -1)).ToArray();
    }
}
