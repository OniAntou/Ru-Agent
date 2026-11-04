using RuAgent.Protocol.Harness;

namespace RuApp.Services;

/// <summary>Owns submission snapshot and accepted-draft commit rules.</summary>
internal static class ChatSubmissionPresenter
{
    internal sealed record RunSubmissionSnapshot(
        string SessionId,
        long SessionGeneration,
        CreateRunProviderSelection ProviderSelection);

    /// <summary>Snapshots run intent before session admission can yield to other UI work.</summary>
    internal static async Task<RunSubmissionSnapshot?> CaptureRunSubmissionAsync(
        Func<CreateRunProviderSelection?> captureProviderSelection,
        Func<Task> ensureSession,
        Func<string> readSession,
        Func<long>? readSessionGeneration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureProviderSelection);
        ArgumentNullException.ThrowIfNull(ensureSession);
        ArgumentNullException.ThrowIfNull(readSession);
        cancellationToken.ThrowIfCancellationRequested();
        var providerSelection = captureProviderSelection();
        if (providerSelection is null) return null;
        await ensureSession().ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return new RunSubmissionSnapshot(readSession(), readSessionGeneration?.Invoke() ?? 0, providerSelection);
    }

    internal static bool OwnsAdmission(
        RunSubmissionSnapshot snapshot,
        Func<string> readSession,
        Func<long> readSessionGeneration) =>
        string.Equals(snapshot.SessionId, readSession(), StringComparison.Ordinal)
        && snapshot.SessionGeneration == readSessionGeneration();

    /// <summary>
    /// Submits a turn and clears the draft the moment the Host accepts it.
    /// </summary>
    /// <remarks>
    /// <paramref name="submit"/> receives the acceptance callback and completes only
    /// when the run ends or parks, so clearing on its return would leave the sent text
    /// in the composer for the whole run, where Enter would queue it again. A send that
    /// is never accepted (declined Full Access, a run already in hand, a failure) never
    /// invokes the callback and keeps the draft. The snapshot guard is evaluated at
    /// acceptance, so text typed while the send awaited the Host survives. The clear is
    /// one-shot because a session-recovery retry reuses the same callback.
    /// </remarks>
    internal static async Task<bool> SubmitAsync(
        Func<Action, Task<bool>> submit,
        Func<bool> snapshotIsCurrent,
        Action clearDraft)
    {
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(snapshotIsCurrent);
        ArgumentNullException.ThrowIfNull(clearDraft);
        bool acceptanceHandled = false;
        return await submit(() =>
        {
            if (acceptanceHandled) return;
            acceptanceHandled = true;
            if (snapshotIsCurrent()) clearDraft();
        }).ConfigureAwait(true);
    }
}
