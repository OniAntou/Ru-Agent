using RuAgent.Protocol;

namespace RuAgent.Broker.Subagents.Workspaces;

internal static class SubagentWorkspaceFailureCleanup
{
    public static void Run(Action<CancellationToken> reclaim, string code, Exception original)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            reclaim(cleanup.Token);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or AgentProtocolException or OperationCanceledException)
        {
            throw new SubagentWorkspaceException(code, new AggregateException(original, failure));
        }
    }
}
