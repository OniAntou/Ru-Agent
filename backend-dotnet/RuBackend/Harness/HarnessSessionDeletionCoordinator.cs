using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;
using RuBackend.Harness.Policy;

namespace RuBackend.Harness;

public enum HarnessSessionDeletionOutcome
{
    Deleted,
    SessionNotFound,
    ArtifactsPending,
    SessionDeleteFailed,
}

public interface IHarnessSessionDeletionCoordinator
{
    Task<HarnessSessionDeletionOutcome> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

public sealed class HarnessSessionDeletionCoordinator : IHarnessSessionDeletionCoordinator
{
    private readonly IHarnessSessionArtifactStore _artifacts;
    private readonly ISessionStore _sessions;
    private readonly TimeProvider _clock;
    private readonly Broker.IToolGatewayFactory? _brokerGateways;
    private readonly IAgentRuntimeRunCleanup? _runCleanup;
    private readonly Subagents.SubagentCancellationCoordinator? _subagentCancellation;
    private readonly Subagents.SubagentArtifactCleanupCoordinator? _subagentArtifacts;

    public HarnessSessionDeletionCoordinator(
        IHarnessSessionArtifactStore artifacts,
        ISessionStore sessions,
        TimeProvider clock,
        Broker.IToolGatewayFactory? brokerGateways = null,
        IAgentRuntimeRunCleanup? runCleanup = null,
        Subagents.SubagentCancellationCoordinator? subagentCancellation = null,
        Subagents.SubagentArtifactCleanupCoordinator? subagentArtifacts = null)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _brokerGateways = brokerGateways;
        _runCleanup = runCleanup;
        _subagentCancellation = subagentCancellation;
        _subagentArtifacts = subagentArtifacts;
    }

    public async Task<HarnessSessionDeletionOutcome> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (_brokerGateways is not null)
        {
            if (!await _brokerGateways.RevokeSessionGrantsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                throw new HarnessStoreException("broker_revoke_rejected");
            }

            // Grants alone are not enough: the session close removes the
            // authority's run leases and clears its tasks, approvals and output.
            await _brokerGateways.CloseSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        // The tombstone must land before any artifact row is removed: a create
        // that read the conversation record before this delete would otherwise insert
        // a run (and recreate the permission row) after cleanup. A failure here
        // fails the whole delete closed, so a session that stays usable keeps no
        // tombstone instead of a delete that leaves a writable hole.
        await _artifacts
            .MarkSessionDeletedAsync(
                sessionId,
                _clock.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);

        // Stop in-flight runs before the artifact store marks them cancelled:
        // changing SQLite alone leaves the tool process running, and the drive
        // loop cannot notice a deleted run while it is blocked inside a step.
        if (_subagentCancellation is not null)
            await _subagentCancellation.CancelSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        bool artifactsDeleted;
        var deletedRunCount = 0;
        try
        {
            if (_runCleanup is not null)
            {
                var runIds = await _artifacts
                    .ListNonTerminalRunIdsAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var runIdValue in runIds)
                {
                    await _runCleanup
                        .CancelAndRemoveRunAsync(new RunId(runIdValue), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (_subagentArtifacts is not null)
                await _subagentArtifacts.ReclaimSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

            var deletedArtifacts = await _artifacts.CloseAndDeleteAsync(
                    sessionId,
                    _clock.GetUtcNow().ToUniversalTime(),
                    cancellationToken)
                .ConfigureAwait(false);
            deletedRunCount = deletedArtifacts.DeletedRuns;
            artifactsDeleted = true;
        }
        catch (HarnessStoreException ex)
        {
            // Harness artifacts are secondary, but their failure must be visible:
            // the conversation record is still deleted (primary), while the caller gets
            // ArtifactsPending so the UI can report partial cleanup and retry.
            System.Diagnostics.Debug.WriteLine($"[Delete] Harness artifacts issue for {sessionId}: {ex.Code}, conversation delete continues");
            artifactsDeleted = false;
        }
        var primary = await _sessions.DeleteWithOutcomeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // Session memory dies with the session: a deleted session starts from
        // zero approvals even if its id were ever observed again.
        SessionApprovalLedger.ClearSession(sessionId);
        if (primary == SessionDeleteOutcome.Failed) return HarnessSessionDeletionOutcome.SessionDeleteFailed;
        if (!artifactsDeleted) return HarnessSessionDeletionOutcome.ArtifactsPending;
        // A retry can finish retained harness cleanup after the conversation
        // record was deleted by the first pass. Report that completed work.
        return primary == SessionDeleteOutcome.Deleted || deletedRunCount > 0
            ? HarnessSessionDeletionOutcome.Deleted : HarnessSessionDeletionOutcome.SessionNotFound;
    }
}
