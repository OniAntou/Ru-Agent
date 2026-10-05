using RuAgent.Client;
using RuApp.Models;

namespace RuApp.Services;

public static class WorkStateHandoffContent
{
    public static string? Create(AgentRunModel source, string? targetWorkspaceId)
    {
        ArgumentNullException.ThrowIfNull(source);
        return WorkStateHandoffFormatter.Format(new WorkStateHandoffSnapshot(
            source.RunId,
            source.SessionId,
            source.WorkspaceId,
            source.WorkStateStatus,
            source.WorkStatePartial,
            source.WorkStateIntentOmitted,
            source.WorkStatePreviewOmitted,
            source.WorkStateSourceRunId,
            source.WorkStateSourceEventId,
            source.WorkStateUpdatedAt,
            source.WorkStatePreview),
            targetWorkspaceId);
    }
}
