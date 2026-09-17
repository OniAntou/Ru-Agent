namespace RuBackend.Business.Sessions;

/// <summary>Latest-run workspace lookup + display label for the session list.</summary>
public static class SessionWorkspaceResolver
{
    public const string LatestWorkspaceSql = """
        SELECT session_id, workspace_id FROM (
          SELECT session_id, workspace_id,
                 ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY created_at_utc DESC, run_id DESC) AS rn
          FROM agent_runs
        ) WHERE rn = 1
        """;

    public static string LabelForRoot(string root)
    {
        var label = Path.GetFileName(root.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(label) ? "workspace" : label;
    }
}
