namespace RuApp.Services;

/// <summary>Owns submission snapshot and accepted-draft commit rules.</summary>
internal static class ChatSubmissionPresenter
{
    internal static async Task<string> CaptureSessionAsync(Func<Task> ensureSession, Func<string> readSession)
    {
        ArgumentNullException.ThrowIfNull(ensureSession);
        ArgumentNullException.ThrowIfNull(readSession);
        await ensureSession().ConfigureAwait(true);
        return readSession();
    }

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
