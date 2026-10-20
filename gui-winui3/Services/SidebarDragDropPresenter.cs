using RuApp.Models;

namespace RuApp.Services;

internal sealed record SidebarDropIntent(string Key, string? Path, string? BeforeId, bool InsertAbove);

/// <summary>Resolves sidebar drop semantics independently of WinUI hit testing.</summary>
internal static class SidebarDragDropPresenter
{
    internal static SidebarDropIntent? Resolve(
        string draggedSessionId,
        object hoveredRow,
        bool insertAbove,
        IReadOnlyList<SessionInfo> sessions,
        IReadOnlyList<WorkspaceGroup> groups,
        Func<SessionInfo, WorkspaceGroupIdentity> groupFor)
    {
        ArgumentNullException.ThrowIfNull(hoveredRow);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(groupFor);

        WorkspaceGroupIdentity? target = hoveredRow switch
        {
            SessionInfo session => groupFor(session),
            WorkspaceGroupRow group => new(group.Group.Key, group.Group.Label, group.Group.Path),
            SidebarSectionRow section when section.IsRecents || section.IsCli => new(section.Key, section.Label, null),
            SidebarEmptyRow empty when empty.Key is SidebarSessionOrganization.RecentsKey or SidebarSessionOrganization.CliKey => new(empty.Key, empty.Label, null),
            WorkspaceShowMoreRow more => GroupIdentity(more.GroupKey, groups),
            _ => null,
        };
        if (target is null) return null;

        var dragged = sessions.FirstOrDefault(session => session.Id == draggedSessionId);
        if (dragged is null || !SidebarSessionOrganization.CanMoveTo(dragged, target.Key)) return null;

        string? beforeId = null;
        if (hoveredRow is SessionInfo hovered)
        {
            if (hovered.Id == draggedSessionId) return null;
            var siblings = groups.FirstOrDefault(group => group.Key == target.Key)?.Sessions ?? [];
            beforeId = insertAbove
                ? hovered.Id
                : siblings.SkipWhile(session => session.Id != hovered.Id).Skip(1)
                    .FirstOrDefault(session => session.Id != draggedSessionId)?.Id;
        }

        return new SidebarDropIntent(target.Key, target.Path, beforeId, insertAbove);
    }

    private static WorkspaceGroupIdentity? GroupIdentity(string key, IReadOnlyList<WorkspaceGroup> groups)
    {
        var group = groups.FirstOrDefault(candidate => candidate.Key == key);
        return group is null ? null : new(group.Key, group.Label, group.Path);
    }
}
