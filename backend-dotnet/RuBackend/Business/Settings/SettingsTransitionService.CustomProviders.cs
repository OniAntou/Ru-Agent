using RuBackend.Providers;

namespace RuBackend.Business.Settings;

public sealed partial class SettingsTransitionService
{
    /// <summary>
    /// Whether this save would leave a key bound to a plain-HTTP endpoint on
    /// another computer (ADR 0034): a new key for such an endpoint, or an
    /// endpoint moved there while its saved key stays. The run path refuses the
    /// same thing; checking here tells the user while they are still editing.
    /// </summary>
    private async Task<bool> SendsKeyInsecurelyAsync(
        SettingsSnapshot previous,
        SettingsSnapshot candidate,
        IReadOnlyList<CredentialUpdate> updates,
        CancellationToken cancellationToken)
    {
        var updated = updates.Select(update => update.Account).ToHashSet(StringComparer.Ordinal);
        var before = CustomProviders.Read(previous).ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        foreach (var provider in CustomProviders.Read(candidate))
        {
            var endpoint = new Uri(provider.BaseUrl);
            if (CustomProviders.AllowsCredential(endpoint)) continue;
            if (updated.Contains(provider.Account)) return true;
            var moved = !before.TryGetValue(provider.Id, out var old) || old.BaseUrl != provider.BaseUrl;
            if (moved && await HasKeyAsync(provider.Account, cancellationToken).ConfigureAwait(false)) return true;
        }

        // A key for a provider the same save does not define cannot be checked: refuse it.
        var defined = CustomProviders.Read(candidate).Select(provider => provider.Account).ToHashSet(StringComparer.Ordinal);
        if (updated.Any(account => account.StartsWith("custom_provider_", StringComparison.Ordinal) && !defined.Contains(account)))
            throw new SettingsPayloadException();

        // The legacy Custom OpenAI slot follows the same rule.
        if (!Uri.TryCreate(candidate.CustomUrl, UriKind.Absolute, out var legacy) || CustomProviders.AllowsCredential(legacy))
            return false;
        if (updated.Contains(CustomAccount)) return true;
        return candidate.CustomUrl != previous.CustomUrl
            && await HasKeyAsync(CustomAccount, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasKeyAsync(string account, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(await _credentials.GetAsync(ServiceName, account, cancellationToken).ConfigureAwait(false));
}
