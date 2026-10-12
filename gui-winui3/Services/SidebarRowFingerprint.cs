namespace RuApp.Services;

/// <summary>Fingerprint of the sidebar session list so periodic polls only
/// re-render when something actually changed (no-change rebuilds visibly flicker).</summary>
public static class SidebarRowFingerprint
{
    public static string ForSessions(IEnumerable<Models.SessionInfo> sessions) =>
        string.Join(
            "\u001F",
            sessions.Select(session => string.Join(
                "\u001E",
                session.Id,
                session.Title,
                session.CreatedAt,
                session.UpdatedAt,
                session.LastAgentActivityAt,
                session.MessageCount,
                session.ParentSessionId,
                session.ForkedFromMessageId,
                session.WorkspaceId,
                session.WorkspaceLabel,
                session.WorkspacePath,
                session.ClientSource)));
}
