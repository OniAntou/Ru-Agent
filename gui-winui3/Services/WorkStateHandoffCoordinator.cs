using RuApp.Models;

namespace RuApp.Services;

public sealed record WorkStateHandoffTarget(
    string SessionId,
    string? WorkspaceId,
    long TurnGeneration,
    long SessionGeneration,
    string DraftText,
    IReadOnlyList<string> AttachmentIds);

public sealed record WorkStateHandoffPreparation(AgentRunModel? SourceRun, string? Content, string? Error)
{
    public bool IsReady => Content is not null && Error is null;
}

public static class WorkStateHandoffCoordinator
{
    public static async Task<WorkStateHandoffPreparation> PrepareSelectedRunAsync(
        string selectedRunId,
        string selectedSessionId,
        string? targetWorkspaceId,
        Func<string, CancellationToken, Task<AgentRunModel?>> getRunAsync,
        Func<bool> isTargetCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getRunAsync);
        ArgumentNullException.ThrowIfNull(isTargetCurrent);
        if (!isTargetCurrent())
        {
            return new(null, null, "The target chat changed before the saved run could be read.");
        }

        try
        {
            var run = await getRunAsync(selectedRunId, cancellationToken).ConfigureAwait(false);
            if (!isTargetCurrent())
            {
                return new(null, null, "The target chat changed while the saved run was being read.");
            }

            var result = Prepare(run, selectedRunId, selectedSessionId, targetWorkspaceId);
            return result.IsReady
                ? new(run, result.Content, null)
                : new(null, null, result.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(null, null, "Reading the selected saved run was cancelled.");
        }
        catch (Exception)
        {
            return new(null, null, "The selected saved run could not be read.");
        }
    }

    public static bool IsCurrent(
        WorkStateHandoffTarget target,
        string currentSessionId,
        string? currentWorkspaceId,
        long currentTurnGeneration,
        long currentSessionGeneration,
        string currentDraftText,
        IReadOnlyList<string> currentAttachmentIds) =>
        target.TurnGeneration == currentTurnGeneration
        && target.SessionGeneration == currentSessionGeneration
        && string.Equals(target.SessionId, currentSessionId, StringComparison.Ordinal)
        && string.Equals(target.WorkspaceId, currentWorkspaceId, StringComparison.Ordinal)
        && string.Equals(target.DraftText, currentDraftText, StringComparison.Ordinal)
        && target.AttachmentIds.SequenceEqual(currentAttachmentIds, StringComparer.Ordinal);

    public static WorkStateHandoffPreparation Prepare(
        AgentRunModel? selectedRun,
        string selectedRunId,
        string selectedSessionId,
        string? targetWorkspaceId)
    {
        if (selectedRun is null
            || !string.Equals(selectedRun.RunId, selectedRunId, StringComparison.Ordinal)
            || !string.Equals(selectedRun.SessionId, selectedSessionId, StringComparison.Ordinal))
        {
            return new(null, null, "The selected saved run could not be verified in that session.");
        }

        var content = WorkStateHandoffContent.Create(selectedRun, targetWorkspaceId);
        return content is null
            ? new(null, null, "The selected run has no bounded safe work-state preview to attach.")
            : new(selectedRun, content, null);
    }
}
