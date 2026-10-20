using RuBackend.Business.Settings;

namespace RuBackend.Providers;

public sealed partial class ProviderCatalogService
{
    // Use the normal provider-specific catalog path, but an isolated cache and
    // read-only candidate credential. Never publish the candidate to the vault
    // or let a rejected probe poison the signed-in provider's live catalog.
    public Task<ProviderCatalogSnapshot> CheckCandidateCredentialAsync(
        string alias, string key, SettingsSnapshot settings, CancellationToken ct)
    {
        var account = ProviderSelection.CredentialAccountForAlias(alias)
            ?? throw new ArgumentException("Provider does not use credentials.");
        var probe = new ProviderCatalogService(http, new CandidateCredential(account, key), clock, _modelsDevCacheFile);
        return probe.GetAsync(alias, settings, refresh: true, ct);
    }

    private sealed class CandidateCredential(string account, string key) : ICredentialStore
    {
        public Task<string?> GetAsync(string serviceName, string accountName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(serviceName == "Ru" && accountName == account ? key : null);
        }
        public Task SetAsync(string serviceName, string accountName, string secret, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Candidate credentials are read-only.");
        public Task DeleteAsync(string serviceName, string accountName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Candidate credentials are read-only.");
    }
}
