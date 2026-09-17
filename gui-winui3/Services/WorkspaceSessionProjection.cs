namespace RuApp.Services;

/// <summary>Pure view projection: sessions grouped by workspace for the sidebar.</summary>
public static class WorkspaceSessionProjection
{
    public static WorkspaceProjection Project(
        IReadOnlyList<Models.SessionInfo> sessions,
        string? search)
    {
        var groups = new List<WorkspaceGroup>();
        var byKey = new Dictionary<string, List<Models.SessionInfo>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, (string Label, string? Path)>(StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            var key = string.IsNullOrEmpty(session.WorkspaceId) ? "ungrouped" : session.WorkspaceId;
            var label = string.IsNullOrEmpty(session.WorkspaceLabel) ? "Ungrouped" : session.WorkspaceLabel;
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<Models.SessionInfo>();
                byKey[key] = list;
                meta[key] = (label, session.WorkspacePath);
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

/// <summary>Sidebar row for a workspace header; chevron follows expand state.</summary>
public sealed record WorkspaceGroupRow(WorkspaceGroup Group, bool IsExpanded)
{
    public string ChevronGlyph => IsExpanded ? "" : "";
}

/// <summary>Sidebar row expanding a folded group.</summary>
public sealed record WorkspaceShowMoreRow(string GroupKey, string DisplayText);
