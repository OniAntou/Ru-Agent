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
        try
        {
            await _artifacts.CloseAndDeleteAsync(
                    sessionId,
                    _clock.GetUtcNow().ToUniversalTime(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            // For old chats, harness artifacts are secondary. A transient DB busy/conflict should not block the user's chat file deletion.
            // The harness row will be cleaned on next expiry or successful delete. Keep the session file deletion as primary.
            System.Diagnostics.Debug.WriteLine($"[Delete] Harness artifacts issue for {sessionId}: {ex.Code}, continuing to session file delete");
        }
        return await _sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ? HarnessSessionDeletionOutcome.Deleted
            : HarnessSessionDeletionOutcome.SessionNotFound;
    }
}
