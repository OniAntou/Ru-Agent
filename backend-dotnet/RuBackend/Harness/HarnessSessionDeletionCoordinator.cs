using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;

namespace RuBackend.Harness;

public enum HarnessSessionDeletionOutcome
{
    Deleted,
    SessionNotFound,
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

    public HarnessSessionDeletionCoordinator(
        IHarnessSessionArtifactStore artifacts,
        ISessionStore sessions,
        TimeProvider clock)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<HarnessSessionDeletionOutcome> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await _artifacts.CloseAndDeleteAsync(
                sessionId,
                _clock.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);
        return await _sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ? HarnessSessionDeletionOutcome.Deleted
            : HarnessSessionDeletionOutcome.SessionNotFound;
    }
}
