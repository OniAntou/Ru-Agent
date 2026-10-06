namespace RuAgent.Client;

public static class SessionPresetVersionNegotiator
{
    private const int MaximumAttempts = 4;

    public static async Task<T> ExecuteAsync<T>(
        string sessionId,
        Func<long, Task<T>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(update);
        var expectedVersion = 0L;
        var observedVersions = new HashSet<long> { expectedVersion };
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await update(expectedVersion).ConfigureAwait(false);
            }
            catch (AgentApiException error) when (
                error.StatusCode == 409
                && string.Equals(error.ErrorCode, "preset_version_conflict", StringComparison.Ordinal))
            {
                if (error.PresetSessionId is null
                    || !string.Equals(error.PresetSessionId, sessionId, StringComparison.Ordinal)
                    || error.CurrentPresetVersion is not long currentVersion
                    || currentVersion < 0
                    || !observedVersions.Add(currentVersion)
                    || attempt + 1 == MaximumAttempts)
                {
                    throw;
                }

                expectedVersion = currentVersion;
            }
        }

        throw new AgentApiException("preset_version_conflict", "Preset version could not be reconciled.", 409);
    }
}
