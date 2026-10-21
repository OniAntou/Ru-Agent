using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>
/// The Host calls the settings editor makes. A seam rather than <see cref="ApiService"/>
/// itself so the editor's decisions can be exercised without a running Host.
/// </summary>
internal interface ISettingsBackend
{
    Task<AppSettingsDto?> GetSettingsAsync();

    Task<IReadOnlyList<ModelsDevEntryDto>> GetModelsDevAsync();

    Task<IReadOnlyList<string>> GetModelsDevKeysAsync();

    Task<bool> SaveProviderSettingsAsync(ProviderSettingsUpdateDto settings);

    Task<ProviderKeyProbeResult> ValidateAndSaveModelsDevKeyAsync(string providerId, string key, CancellationToken cancellationToken);

    Task<bool> RemoveProviderAsync(string backendChoice);
}

/// <summary>The provider catalog state the settings editor reads and refreshes.</summary>
internal interface IProviderCatalogSession
{
    event EventHandler<ProviderCatalogState>? StateChanged;

    ProviderCatalogState State { get; }

    void SetBackend(string backend);

    Task<bool> RefreshAsync(bool force = false);
}

internal sealed class ApiSettingsBackend(ApiService api) : ISettingsBackend
{
    public Task<AppSettingsDto?> GetSettingsAsync() => api.GetSettingsAsync();

    public Task<IReadOnlyList<ModelsDevEntryDto>> GetModelsDevAsync() => api.GetModelsDevAsync();

    public Task<IReadOnlyList<string>> GetModelsDevKeysAsync() => api.GetModelsDevKeysAsync();

    public Task<bool> SaveProviderSettingsAsync(ProviderSettingsUpdateDto settings) =>
        api.SaveProviderSettingsAsync(settings);

    public Task<ProviderKeyProbeResult> ValidateAndSaveModelsDevKeyAsync(
        string providerId, string key, CancellationToken cancellationToken) =>
        api.ValidateAndSaveModelsDevKeyAsync(providerId, key, cancellationToken);

    public Task<bool> RemoveProviderAsync(string backendChoice) => api.RemoveProviderAsync(backendChoice);
}

internal sealed class ProviderCatalogSession(ProviderCatalogController controller) : IProviderCatalogSession
{
    public event EventHandler<ProviderCatalogState>? StateChanged
    {
        add => controller.StateChanged += value;
        remove => controller.StateChanged -= value;
    }

    public ProviderCatalogState State => controller.State;

    public void SetBackend(string backend) => controller.SetBackend(backend);

    public Task<bool> RefreshAsync(bool force = false) => controller.RefreshAsync(force);
}
