using RuAgent.Broker.Tools.Git;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>
    /// The bounded read-only Git probe Host prompt context may ask for. It is exposed
    /// on the runtime (not the tool pipeline) on purpose: the read is fixed, read-only
    /// and has no model-chosen arguments, so it neither spends tool budget nor creates
    /// an execution receipt. Tests may replace it with a fake <see cref="IGitCli"/>.
    /// </summary>
    public WorkspaceGitProbe WorkspaceGitProbe { get; init; } = new();

    /// <summary>
    /// Reads the run workspace's repository facts. Null means unavailable; it never
    /// makes a clean or dirty claim the probe could not observe.
    /// </summary>
    public Task<WorkspaceGitSnapshot?> WorkspaceGitSnapshotAsync(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        return WorkspaceGitProbe.ReadAsync(canonicalRoot, cancellationToken);
    }
}
