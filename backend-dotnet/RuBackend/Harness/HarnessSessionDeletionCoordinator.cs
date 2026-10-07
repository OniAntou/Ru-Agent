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
    private readonly Broker.BrokerToolGatewayFactory? _brokerGateways;
    private readonly IAgentRuntimeRunCleanup? _runCleanup;

    public HarnessSessionDeletionCoordinator(
        IHarnessSessionArtifactStore artifacts,
        ISessionStore sessions,
        TimeProvider clock,
        Broker.BrokerToolGatewayFactory? brokerGateways = null,
        IAgentRuntimeRunCleanup? runCleanup = null)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _brokerGateways = brokerGateways;
        _runCleanup = runCleanup;
    }

    public async Task<HarnessSessionDeletionOutcome> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (_brokerGateways is not null
            && !await _brokerGateways.RevokeSessionGrantsAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            throw new HarnessStoreException("broker_revoke_rejected");
        }

        // The tombstone must land before any artifact row is removed: a create
        // that read the session file before this delete would otherwise insert
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
        bool artifactsDeleted;
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

            await _artifacts.CloseAndDeleteAsync(
                    sessionId,
                    _clock.GetUtcNow().ToUniversalTime(),
                    cancellationToken)
                .ConfigureAwait(false);
            artifactsDeleted = true;
        }
        catch (HarnessStoreException ex)
        {
            // Harness artifacts are secondary, but their failure must be visible:
            // the session file is still deleted (primary), while the caller gets
            // ArtifactsPending so the UI can report partial cleanup and retry.
            // The leftover harness row is also reclaimed on next expiry.
            System.Diagnostics.Debug.WriteLine($"[Delete] Harness artifacts issue for {sessionId}: {ex.Code}, session file delete continues");
            artifactsDeleted = false;
        }
        var sessionDeleted = await _sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!sessionDeleted)
        {
            return HarnessSessionDeletionOutcome.SessionNotFound;
        }

        // Session memory dies with the session: a deleted session starts from
        // zero approvals even if its id were ever observed again.
        SessionApprovalLedger.ClearSession(sessionId);
        return artifactsDeleted
            ? HarnessSessionDeletionOutcome.Deleted
            : HarnessSessionDeletionOutcome.ArtifactsPending;
    }
}
