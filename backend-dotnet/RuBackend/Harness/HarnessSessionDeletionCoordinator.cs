using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;

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

    public HarnessSessionDeletionCoordinator(
        IHarnessSessionArtifactStore artifacts,
        ISessionStore sessions,
        TimeProvider clock,
        Broker.BrokerToolGatewayFactory? brokerGateways = null)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _brokerGateways = brokerGateways;
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

        bool artifactsDeleted;
        try
        {
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
        return artifactsDeleted
            ? HarnessSessionDeletionOutcome.Deleted
            : HarnessSessionDeletionOutcome.ArtifactsPending;
    }
}
