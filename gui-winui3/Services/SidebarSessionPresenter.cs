using RuApp.Models;

namespace RuApp.Services;

internal sealed record SidebarSessionProjection(IReadOnlyList<WorkspaceGroup> Groups, IReadOnlyList<object> Rows, int MatchCount);

/// <summary>
/// Builds the sidebar's ordered and filtered row model. MainWindow only reconciles
/// the returned rows with WinUI containers and updates selection/scroll state.
/// </summary>
internal static class SidebarSessionPresenter
{
    internal static string ResolveNewChatWorkspace(string? selectedWorkspaceKey) =>
        selectedWorkspaceKey ?? SidebarSessionOrganization.RecentsKey;

    internal static bool NextExpandedState(bool? boundIsExpanded, bool storedDefault) =>
        !(boundIsExpanded ?? storedDefault);

    internal static bool TryMove(
        SidebarOrganizationStore store,
        IReadOnlyList<SessionInfo> sessions,
        SidebarOrganizationPreferences preferences,
        string sessionId,
        string key,
        string? path,
        string? beforeId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(preferences);
        var session = sessions.FirstOrDefault(row => row.Id == sessionId);
        if (session is null || !SidebarSessionOrganization.CanMoveTo(session, key)) return false;
        var orderedIds = SidebarSessionOrganization.Order(sessions, preferences).Select(row => row.Id).ToArray();
        return store.Move(sessionId, path, beforeId, orderedIds);
    }

    internal static SidebarSessionProjection Build(
        IReadOnlyList<SessionInfo> source,
        string? search,
        string? appRoot,
        SidebarOrganizationPreferences organization,
        IReadOnlyList<string> pinnedPaths,
        string? activeSessionId,
        bool activeWorkspaceFilter,
        Func<string, bool, bool> expanded,
        Func<string, bool> showAll,
        int pageSize = 5)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(pinnedPaths);
        ArgumentNullException.ThrowIfNull(expanded);
        ArgumentNullException.ThrowIfNull(showAll);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var sessions = SidebarSessionOrganization.Order(source, organization).ToList();
        WorkspaceGroupIdentity GroupFor(SessionInfo session) =>
            SidebarSessionOrganization.GroupFor(session, appRoot, organization);
        var active = sessions.FirstOrDefault(session => session.Id == activeSessionId);
        var activeKey = active is null ? null : GroupFor(active).Key;
        if (activeWorkspaceFilter && activeKey is not null)
            sessions = sessions.Where(session => GroupFor(session).Key == activeKey).ToList();

        bool searching = !string.IsNullOrWhiteSpace(search);
        var projected = WorkspaceSessionProjection.Project(sessions, search, GroupFor);
        var groups = WorkspaceSessionProjection.MergePinned(
            projected.Groups,
            pinnedPaths.Where(path => !SidebarSessionOrganization.SamePath(path, appRoot)).ToArray());
        if (searching) groups = groups.Where(group => group.Sessions.Count > 0).ToArray();

        var rows = new List<object>();
        var recents = groups.FirstOrDefault(group => group.Key == SidebarSessionOrganization.RecentsKey)
            ?? new WorkspaceGroup(SidebarSessionOrganization.RecentsKey, "Recents", null, []);
        bool recentExpanded = searching || expanded(recents.Key, true);
        rows.Add(new SidebarSectionRow(SidebarSessionOrganization.WorkspacesKey, "WORKSPACES", FilterActive: activeWorkspaceFilter));

        bool firstWorkspace = true;
        foreach (var group in groups.Where(group => group.Path is not null))
        {
            bool defaultExpanded = activeKey is null ? firstWorkspace : group.Key == activeKey;
            bool isExpanded = searching || expanded(group.Key, defaultExpanded) ||
                (group.Path is { Length: > 0 } path && expanded(PathStateKey(path), false));
            firstWorkspace = false;
            rows.Add(new WorkspaceGroupRow(group, isExpanded));
            AppendSessions(rows, group, isExpanded, showAll(group.Key), activeSessionId, pageSize);
        }

        // An empty WORKSPACES header with its tools beside it read as a broken section
        // while the chats sat under RECENTS. Say that the section is simply empty.
        if (firstWorkspace && !searching)
        {
            rows.Add(new SidebarEmptyRow(SidebarSessionOrganization.WorkspacesKey, NoWorkspacesLabel));
        }

        rows.Add(new SidebarSectionRow(recents.Key, "RECENTS", recentExpanded));
        AppendSessions(rows, recents, recentExpanded, showAll(recents.Key), activeSessionId, pageSize);
        var cli = groups.FirstOrDefault(group => group.Key == SidebarSessionOrganization.CliKey)
            ?? new WorkspaceGroup(SidebarSessionOrganization.CliKey, "CLI Session", null, []);
        bool cliExpanded = searching || expanded(cli.Key, true);
        rows.Add(new SidebarSectionRow(cli.Key, "CLI SESSIONS", cliExpanded));
        AppendSessions(rows, cli, cliExpanded, showAll(cli.Key), activeSessionId, pageSize);

        return new SidebarSessionProjection(groups, rows, rows.OfType<SessionInfo>().Count());
    }

    internal const string NoWorkspacesLabel = "No other project folders yet";

    internal static string PathStateKey(string canonicalPath) =>
        "path:" + canonicalPath.ToLowerInvariant();

    private static void AppendSessions(
        List<object> rows,
        WorkspaceGroup group,
        bool isExpanded,
        bool requestedShowAll,
        string? activeSessionId,
        int pageSize)
    {
        if (!isExpanded) return;
        bool showAll = requestedShowAll || group.Sessions.Skip(pageSize).Any(session => session.Id == activeSessionId);
        rows.AddRange(showAll ? group.Sessions : group.Sessions.Take(pageSize));
        if (!showAll && group.Sessions.Count > pageSize)
            rows.Add(new WorkspaceShowMoreRow(group.Key, $"Show {group.Sessions.Count - pageSize} more"));
        if (group.Sessions.Count == 0 && group.Path is null)
            rows.Add(new SidebarEmptyRow(group.Key,
                group.Key == SidebarSessionOrganization.CliKey ? "No CLI sessions" : "No recent sessions"));
    }
}
