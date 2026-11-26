using RuAgent.Broker.Tools.Git;

namespace RuBackend.Harness.Broker;

public sealed partial class InProcessToolGateway
{
    /// <summary>
    /// The run lease's bounded read-only repository facts for prompt context. The
    /// canonical root comes from the lease, never from the caller.
    /// </summary>
    public Task<WorkspaceGitSnapshot?> WorkspaceGitSnapshotAsync(CancellationToken cancellationToken)
    {
        string canonicalRoot;
        lock (_sync)
        {
            canonicalRoot = _lease.CanonicalRoot;
        }

        return _runtime.WorkspaceGitSnapshotAsync(canonicalRoot, cancellationToken);
    }
}
