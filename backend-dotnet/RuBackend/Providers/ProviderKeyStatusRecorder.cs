using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>
/// Turns what model steps learn about a run's key into its stored status: a completed
/// step proves the key, and an HTTP 401 shows it was refused.
/// </summary>
/// <remarks>
/// Observation never delays or fails a step: callers fire it without awaiting, and every
/// failure here is logged and dropped. A verdict is recorded only for the key and
/// endpoint the run actually used (a run keeps its key after a settings change), and
/// only when it differs from what is already stored, so a working run costs one small
/// settings read per step and no write.
/// </remarks>
public sealed class ProviderKeyStatusRecorder(
    SqliteAgentRunProviderSelectionStore pins,
    ISettingsTransitionService transition,
    ILogger<ProviderKeyStatusRecorder>? logger = null)
{
    public void Observe(RunId runId, RuAgent.Runtime.Adapters.Bootstrap.ProviderBootstrapData sentWith, bool accepted) =>
        _ = ObserveAsync(runId, sentWith, accepted);

    internal async Task ObserveAsync(RunId runId, RuAgent.Runtime.Adapters.Bootstrap.ProviderBootstrapData sentWith, bool accepted)
    {
        try
        {
            var state = accepted ? ProviderKeyStatus.Verified : ProviderKeyStatus.Rejected;
            if (string.IsNullOrEmpty(sentWith.Credential)) return;
            var pin = (await pins.GetAsync(runId, CancellationToken.None).ConfigureAwait(false))?.Selection;
            if (pin?.Account is not { Length: > 0 } account) return;
            await transition.RecordKeyStatusAsync(pin.Backend, account, sentWith.Credential, state,
                current => EndpointUnchanged(current, pin), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger?.LogDebug("provider key status not recorded: {Error}", error.GetType().Name);
        }
    }

    /// <summary>
    /// Whether the stored settings still point this backend where the run went. Only
    /// endpoints the user edits can move; catalog providers keep theirs.
    /// </summary>
    internal static bool EndpointUnchanged(SettingsSnapshot current, AgentRunProviderSelection pin)
    {
        static string Trimmed(string? value) => (value ?? string.Empty).Trim().TrimEnd('/');
        if (pin.Backend == "Custom OpenAI")
            return string.Equals(Trimmed(current.CustomUrl), Trimmed(pin.BaseUri), StringComparison.OrdinalIgnoreCase);
        if (CustomProviders.NormalizeBackend(pin.Backend) is { } custom)
            return CustomProviders.Find(current, custom) is { } definition
                && string.Equals(Trimmed(definition.BaseUrl), Trimmed(pin.BaseUri), StringComparison.OrdinalIgnoreCase);
        if (string.Equals(pin.Alias, ProviderOverrides.CloudflareId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return string.Equals(Trimmed(ProviderOverrides.CloudflareBaseUrl(current.CloudflareAccountId)),
                    Trimmed(pin.BaseUri), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return true;
    }
}
