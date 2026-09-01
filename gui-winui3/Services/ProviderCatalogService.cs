using RuApp.Models;

namespace RuApp.Services;

public sealed class ProviderCatalogService
{
    private readonly Func<string, bool, CancellationToken, Task<ProviderCatalogSnapshot?>> _fetch;

    public ProviderCatalogService(ApiService apiService)
        : this((backend, refresh, cancellationToken) =>
            apiService.GetProviderCatalogAsync(backend, refresh, cancellationToken))
    {
    }

    internal ProviderCatalogService(
        Func<string, bool, CancellationToken, Task<ProviderCatalogSnapshot?>> fetch)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
    }

    public Task<ProviderCatalogSnapshot?> GetAsync(
        string backend,
        bool refresh,
        CancellationToken cancellationToken) =>
        _fetch(backend, refresh, cancellationToken);
}
