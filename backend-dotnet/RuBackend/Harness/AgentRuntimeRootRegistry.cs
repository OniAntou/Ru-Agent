using System.Collections.Concurrent;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;

namespace RuBackend.Harness;

/// <summary>
/// Registers the workspace root a run drives against. Roots are process-local: after a
/// host restart the loop skips driving until the run is reconciled (phase 3 Task 8).
/// </summary>
public interface IAgentRuntimeRootRegistry
{
    void Register(RunId runId, string workspaceRoot);

    void Register(RunId runId, string workspaceRoot, PolicySnapshot policySnapshot);

    bool TryGetRoot(RunId runId, out string? workspaceRoot);

    bool TryGetPolicySnapshot(RunId runId, out PolicySnapshot? policySnapshot);

    void RegisterCoordinator(RunId runId, AgentRunCoordinator coordinator);

    bool TryGetCoordinator(RunId runId, out AgentRunCoordinator? coordinator);

    Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken);

    void Remove(RunId runId);

    IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot();
}

public sealed class AgentRuntimeRootRegistry : IAgentRuntimeRootRegistry
{
    private sealed record Registration(
        string WorkspaceRoot,
        PolicySnapshot? PolicySnapshot,
        AgentRunCoordinator? Coordinator = null);

    private readonly ConcurrentDictionary<string, Registration> _roots = new(StringComparer.Ordinal);
    private readonly AgentRuntimeWakeSignal? _wake;

    public AgentRuntimeRootRegistry()
    {
    }

    /// <param name="wake">Woken on every registration so the loop drives a new run at once.</param>
    public AgentRuntimeRootRegistry(AgentRuntimeWakeSignal wake)
    {
        _wake = wake ?? throw new ArgumentNullException(nameof(wake));
    }

    public void Register(RunId runId, string workspaceRoot)
    {
        _roots.AddOrUpdate(
            runId.Value,
            _ => new Registration(workspaceRoot, null),
            (_, existing) => existing with { WorkspaceRoot = workspaceRoot });
        _wake?.Wake();
    }

    public void Register(RunId runId, string workspaceRoot, PolicySnapshot policySnapshot)
    {
        ArgumentNullException.ThrowIfNull(policySnapshot);
        _roots.AddOrUpdate(
            runId.Value,
            _ => new Registration(workspaceRoot, policySnapshot),
            (_, existing) => existing with
            {
                WorkspaceRoot = workspaceRoot,
                PolicySnapshot = policySnapshot,
            });
        _wake?.Wake();
    }

    public bool TryGetRoot(RunId runId, out string? workspaceRoot)
    {
        if (_roots.TryGetValue(runId.Value, out var registration))
        {
            workspaceRoot = registration.WorkspaceRoot;
            return true;
        }

        workspaceRoot = null;
        return false;
    }

    public bool TryGetPolicySnapshot(RunId runId, out PolicySnapshot? policySnapshot)
    {
        if (_roots.TryGetValue(runId.Value, out var registration)
            && registration.PolicySnapshot is { } snapshot)
        {
            policySnapshot = snapshot;
            return true;
        }

        policySnapshot = null;
        return false;
    }

    public void RegisterCoordinator(RunId runId, AgentRunCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        while (_roots.TryGetValue(runId.Value, out var existing))
        {
            if (_roots.TryUpdate(
                    runId.Value,
                    existing with { Coordinator = coordinator },
                    existing))
            {
                return;
            }
        }
    }

    public bool TryGetCoordinator(RunId runId, out AgentRunCoordinator? coordinator)
    {
        if (_roots.TryGetValue(runId.Value, out var registration)
            && registration.Coordinator is { } value)
        {
            coordinator = value;
            return true;
        }

        coordinator = null;
        return false;
    }

    public async Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken)
    {
        if (_roots.TryGetValue(runId.Value, out var registration)
            && registration.Coordinator is { } coordinator)
        {
            await coordinator.CancelRunningToolsAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Remove(RunId runId) => _roots.TryRemove(runId.Value, out _);

    public IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot() =>
        _roots.Select(pair => (pair.Key, pair.Value.WorkspaceRoot)).ToArray();
}
