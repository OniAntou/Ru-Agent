using RuAgent.Client;

namespace RuAgent.Cli;

internal sealed record ChatRunReleaseResult(string Message, bool IsError, string? ReleasedRunId = null);

internal static class ChatRunRelease
{
    internal static async Task<ChatRunReleaseResult> ReleaseAsync(AgentApiClient client,
        ChatRunSession owner, string sessionId, string? requestedRunId, CancellationToken token)
    {
        var target = string.IsNullOrWhiteSpace(requestedRunId) ? owner.ParkedRunId : requestedRunId.Trim();
        if (string.IsNullOrEmpty(target)) return new("no parked run · nothing to release", false);
        try
        {
            var run = await client.GetRunAsync(target, token).ConfigureAwait(false);
            if (run.SessionId != sessionId)
                return new("run_session_mismatch: /release only accepts a run in this session", true);
            if (run.TerminalAt is not null || run.Status is "completed" or "cancelled" or "failed" or "interrupted")
            {
                owner.ForgetParked(target);
                if (owner.ActiveRunId == target) owner.ForgetCompletedRun();
                return new($"run already finished · {target}", false, target);
            }
            if (run.Status is not ("waiting_user" or "reconciliation_required"))
                return new($"run_not_parked: run {target} is {run.Status}", true);

            await client.CancelAsync(target, token).ConfigureAwait(false);
            owner.ForgetParked(target);
            if (owner.ActiveRunId == target) owner.ForgetCompletedRun();
            return new($"run released · {target} · this session can accept a new prompt", false, target);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is AgentApiException or HttpRequestException or IOException)
        {
            return new($"release_error: {CliErrorText.ShortMessage(error.Message)}", true);
        }
    }
}
