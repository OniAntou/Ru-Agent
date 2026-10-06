using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Attachments;

public static class WorkStateHandoffContent
{
    public static string? Create(GetAgentRunResponse run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return WorkStateHandoffFormatter.Format(new WorkStateHandoffSnapshot(
            run.RunId,
            run.SessionId,
            run.WorkspaceId,
            run.WorkStateStatus,
            run.WorkStatePartial,
            run.WorkStateIntentOmitted,
            run.WorkStatePreviewOmitted,
            run.WorkStateSourceRunId,
            run.WorkStateSourceEventId,
            run.WorkStateUpdatedAt,
            run.WorkStatePreview),
            targetWorkspaceId: null);
    }
}
