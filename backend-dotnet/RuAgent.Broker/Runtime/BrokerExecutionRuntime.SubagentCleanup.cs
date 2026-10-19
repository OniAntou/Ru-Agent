using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    private readonly SemaphoreSlim _subagentArtifacts = new(1, 1);

    public Task ReclaimSubagentWorkspaceAsync(SubagentCollectionSource source, CancellationToken ct) =>
        UnderSubagentArtifactsAsync(async () =>
        {
            var manifest = ValidateCollectionSource(source, 1, 0);
            await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_session.Owns(source.InternalSessionId)
                    || _session.HasRunWithinDirectory(Path.GetDirectoryName(manifest.Snapshot.BaseDirectory)!)
                    || _session.HasRunWithinDirectory(manifest.GitDirectory))
                    throw new AgentProtocolException("subagent_cleanup_busy");
                try { SubagentWorkspaceReclaimer.Reclaim(manifest, source.StateDirectory, ct); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    && error is not SubagentWorkspaceException)
                {
                    throw new SubagentWorkspaceException("subagent_cleanup_incomplete", error);
                }
                return true;
            }
            finally { _lifecycleGate.Release(); }
        }, ct);

    private async Task<T> UnderSubagentArtifactsAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _subagentArtifacts.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await action().ConfigureAwait(false);
        }
        finally { _subagentArtifacts.Release(); }
    }
}
