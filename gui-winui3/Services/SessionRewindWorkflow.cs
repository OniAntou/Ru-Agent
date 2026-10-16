namespace RuApp.Services;

/// <summary>Rewinds only a persisted message and publishes into the session that requested it.</summary>
internal static class SessionRewindWorkflow
{
    internal static async Task<bool> RunAsync(
        string sessionId, int? messageIndex, Func<bool> ownsSession,
        Func<string, int, Task<bool>> rewind, Func<Task<bool>> reload)
    {
        if (messageIndex is null or < 0 || !ownsSession()) return false;
        if (!await rewind(sessionId, messageIndex.Value) || !ownsSession()) return false;
        return await reload() && ownsSession();
    }
}
