using System.Collections.Concurrent;
using RuAgent.Harness.Core;

namespace RuBackend.Harness;

/// <summary>
/// Process-local state used to park runs that need client input and avoid
/// rehydrating the same durable approval until its run version changes.
/// </summary>
internal sealed class AgentRuntimeParkingState
{
    private readonly ConcurrentDictionary<string, string> _recoveredApprovalIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _parkedApprovalVersions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _waitingOnClient = new(StringComparer.Ordinal);

    internal bool IsWaitingOnClient(string runId) => _waitingOnClient.ContainsKey(runId);

    internal void MarkWaitingOnClient(string runId) => _waitingOnClient.TryAdd(runId, 0);

    internal bool IsParkedApprovalVersion(string runId, long version) =>
        _parkedApprovalVersions.TryGetValue(runId, out var parkedVersion)
        && parkedVersion == version;

    internal bool IsRecoveredApproval(string runId, string approvalId) =>
        _recoveredApprovalIds.TryGetValue(runId, out var recoveredApprovalId)
        && string.Equals(recoveredApprovalId, approvalId, StringComparison.Ordinal);

    internal void RecordRecoveredApproval(string runId, string approvalId) =>
        _recoveredApprovalIds[runId] = approvalId;

    internal void ClearRecoveredApproval(string runId) => _recoveredApprovalIds.TryRemove(runId, out _);

    internal void RecordParkedApprovalVersion(string runId, long version)
    {
        if (_recoveredApprovalIds.ContainsKey(runId))
        {
            _parkedApprovalVersions[runId] = version;
        }
    }

    /// <summary>Clears the client wait and approval-version fence before driving a changed run.</summary>
    internal void MarkRunnable(string runId)
    {
        _waitingOnClient.TryRemove(runId, out _);
        _parkedApprovalVersions.TryRemove(runId, out _);
    }

    internal void ObserveClientWaitState(string runId, HarnessRunState state)
    {
        if (state is HarnessRunState.WaitingUser
            or HarnessRunState.WaitingWorkspaceTrust
            or HarnessRunState.ReconciliationRequired)
        {
            MarkWaitingOnClient(runId);
        }
        else
        {
            _waitingOnClient.TryRemove(runId, out _);
        }
    }

    internal void ForgetRun(string runId)
    {
        _recoveredApprovalIds.TryRemove(runId, out _);
        _parkedApprovalVersions.TryRemove(runId, out _);
        _waitingOnClient.TryRemove(runId, out _);
    }
}
