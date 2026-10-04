namespace RuBackend.Business.Sessions;

/// <summary>Latest-run workspace lookup + display label for the session list.</summary>
public static class SessionWorkspaceResolver
{
    public const int MaximumRequestedSessions = 500;

    public static string BuildLatestWorkspaceSql(int sessionCount)
    {
        if (sessionCount is < 1 or > MaximumRequestedSessions)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionCount));
        }

        var requestedSessions = string.Join(", ", Enumerable.Range(0, sessionCount)
            .Select(index => $"($session_{index})"));
        return $"""
            WITH requested(session_id) AS (VALUES {requestedSessions})
            SELECT requested.session_id, latest.workspace_id
            FROM requested
            JOIN agent_runs AS latest ON latest.run_id = (
                SELECT candidate.run_id
                FROM agent_runs AS candidate
                WHERE candidate.session_id = requested.session_id
                ORDER BY candidate.created_at_utc DESC, candidate.run_id DESC
                LIMIT 1)
            """;
    }

    public static string LabelForRoot(string root)
    {
        var label = Path.GetFileName(root.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(label) ? "workspace" : label;
    }
}
