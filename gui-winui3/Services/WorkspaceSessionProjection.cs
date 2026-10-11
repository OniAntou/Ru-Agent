namespace RuApp.Services;

/// <summary>Pure view projection: sessions grouped by workspace for the sidebar.</summary>
public static class WorkspaceSessionProjection
{
    public const string UngroupedKey = "ungrouped";

    public static string KeyFor(Models.SessionInfo session) =>
        string.IsNullOrEmpty(session.WorkspaceId) ? UngroupedKey : session.WorkspaceId;

    public static string LabelForPath(string path)
    {
        var label = System.IO.Path.GetFileName(
            path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(label) ? path : label;
    }

    /// <summary>Merges pinned directories ahead of server groups; a pin matching
    /// a server group by canonical path folds into it, others render empty.</summary>
    public static IReadOnlyList<WorkspaceGroup> MergePinned(
        IReadOnlyList<WorkspaceGroup> serverGroups,
        IReadOnlyList<string> pinnedPaths)
    {
        var merged = new List<WorkspaceGroup>();
        var consumed = new HashSet<WorkspaceGroup>();
        foreach (var pinned in pinnedPaths)
        {
            WorkspaceGroup? match = null;
            foreach (var group in serverGroups)
            {
                if (consumed.Contains(group))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(group.Path)
                    && string.Equals(group.Path, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    match = group;
                    break;
                }
            }

            if (match is not null)
            {
                consumed.Add(match);
                merged.Add(match);
            }
            else
            {
                merged.Add(new WorkspaceGroup(
                    "path:" + pinned.ToLowerInvariant(),
                    LabelForPath(pinned),
                    pinned,
                    []));
            }
        }

        foreach (var group in serverGroups)
        {
            if (!consumed.Contains(group))
            {
                merged.Add(group);
            }
        }

        return merged;
    }

    public static WorkspaceProjection Project(
        IReadOnlyList<Models.SessionInfo> sessions,
        string? search,
        Func<Models.SessionInfo, WorkspaceGroupIdentity>? groupFor = null)
    {
        var groups = new List<WorkspaceGroup>();
        var byKey = new Dictionary<string, List<Models.SessionInfo>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, (string Label, string? Path)>(StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            var identity = groupFor?.Invoke(session);
            var key = identity?.Key ?? KeyFor(session);
            var label = identity?.Label ?? (string.IsNullOrEmpty(session.WorkspaceLabel) ? "Ungrouped" : session.WorkspaceLabel);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<Models.SessionInfo>();
                byKey[key] = list;
                meta[key] = (label, identity is null ? session.WorkspacePath : identity.Path);
            }

            if (Matches(session, label, search))
            {
                list.Add(session);
            }
        }

        foreach (var (key, list) in byKey)
        {
            if (list.Count == 0)
            {
                continue;
            }

            groups.Add(new WorkspaceGroup(key, meta[key].Label, meta[key].Path, list));
        }

        return new WorkspaceProjection(groups);
    }

    private static bool Matches(Models.SessionInfo session, string label, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return session.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || label.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (session.WorkspacePath?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }
}

public sealed record WorkspaceGroup(
    string Key,
    string Label,
    string? Path,
    IReadOnlyList<Models.SessionInfo> Sessions);

public sealed record WorkspaceProjection(IReadOnlyList<WorkspaceGroup> Groups);

public sealed record WorkspaceGroupIdentity(string Key, string Label, string? Path);

public sealed record SidebarSectionRow(string Key, string Label, bool IsExpanded = true, bool FilterActive = false)
{
    public bool IsRecents => Key == SidebarSessionOrganization.RecentsKey;
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE70E";
}

public sealed record SidebarEmptyRow(string Key, string Label);

/// <summary>Sidebar row for a workspace header; chevron follows expand state.</summary>
public sealed record WorkspaceGroupRow(WorkspaceGroup Group, bool IsExpanded)
{
    public string ChevronGlyph => IsExpanded ? "" : "";
}

/// <summary>Sidebar row expanding a folded group.</summary>
public sealed record WorkspaceShowMoreRow(string GroupKey, string DisplayText);
