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

    internal static async Task<bool> SubmitAsync(
        Func<Action, Task<bool>> submit,
        Func<bool> snapshotIsCurrent,
        Action clearDraft)
    {
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(snapshotIsCurrent);
        ArgumentNullException.ThrowIfNull(clearDraft);
        bool acceptancePublished = false;
        bool accepted = await submit(() => acceptancePublished = true).ConfigureAwait(true);
        if (accepted && acceptancePublished && snapshotIsCurrent()) clearDraft();
        return accepted;
    }
}
