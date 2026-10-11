using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;

namespace RuApp;

public sealed partial class MainWindow
{
    private string? _sidebarAppRoot;
    private readonly SidebarOrganizationStore _sidebarOrganization = new();

    private WorkspaceGroupIdentity SidebarGroupFor(SessionInfo session) =>
        SidebarSessionOrganization.GroupFor(session, _sidebarAppRoot, _sidebarOrganization.Current);

    private static string SidebarRowKey(object row) => row switch
    {
        SessionInfo session => "session:" + session.Id,
        WorkspaceGroupRow group => "group:" + group.Group.Key,
        SidebarSectionRow section => "section:" + section.Key,
        SidebarEmptyRow empty => "empty:" + empty.Key,
        WorkspaceShowMoreRow more => "more:" + more.GroupKey,
        _ => throw new ArgumentException("Unknown sidebar row", nameof(row)),
    };

    private void ReconcileSidebarRows(IReadOnlyList<object> desired)
    {
        // Preserve containers across refreshes and expansion during a drag. Clearing
        // the collection destroys the source container and cancels native dragging.
        for (var index = 0; index < desired.Count; index++)
        {
            var key = SidebarRowKey(desired[index]);
            var current = -1;
            for (var scan = index; scan < _sidebarRows.Count; scan++)
                if (SidebarRowKey(_sidebarRows[scan]) == key) { current = scan; break; }
            if (current < 0) _sidebarRows.Insert(index, desired[index]);
            else
            {
                if (current != index) _sidebarRows.Move(current, index);
                if (!Equals(_sidebarRows[index], desired[index])) _sidebarRows[index] = desired[index];
            }
        }
        while (_sidebarRows.Count > desired.Count) _sidebarRows.RemoveAt(_sidebarRows.Count - 1);
    }

    private void AppendSidebarSessions(List<object> rows, WorkspaceGroup group, bool expanded)
    {
        if (!expanded) return;
        var showAll = _groupShowAll.Contains(group.Key)
            || group.Sessions.Skip(WorkspaceGroupPageSize).Any(session => session.Id == _activeSessionId);
        rows.AddRange(showAll ? group.Sessions : group.Sessions.Take(WorkspaceGroupPageSize));
        if (!showAll && group.Sessions.Count > WorkspaceGroupPageSize)
            rows.Add(new WorkspaceShowMoreRow(group.Key, $"Show {group.Sessions.Count - WorkspaceGroupPageSize} more"));
        if (group.Sessions.Count == 0 && group.Key == SidebarSessionOrganization.RecentsKey)
            rows.Add(new SidebarEmptyRow(group.Key, "No recent sessions"));
    }

    private void OnRecentsToggleClick(object sender, RoutedEventArgs e)
    {
        var current = _sidebarRows.OfType<SidebarSectionRow>().FirstOrDefault(row => row.IsRecents);
        _workspaceGroups.SetExpanded(SidebarSessionOrganization.RecentsKey, !(current?.IsExpanded ?? true));
        RenderWorkspaceSidebar();
    }
}
