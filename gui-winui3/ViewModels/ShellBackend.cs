using RuAgent.Hosting;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>What the shell needs from the Host process and the services built on it.</summary>
internal interface IShellBackend
{
    string DisplayName { get; }

    /// <summary>A scripted harness run: never reconnects and reports "Harness ready".</summary>
    bool IsHarness { get; }

    /// <summary>Raised from any thread when the Host process exits.</summary>
    event EventHandler? BackendExited;

    /// <summary>The root the Host runs in, or null when none can be found.</summary>
    string? ResolveProjectRoot();

    Task<BackendSession?> StartAsync(string projectRoot, CancellationToken cancellationToken);

    /// <summary>Rebuilds the Host-bound services and views on a reconnected session.</summary>
    Task RebindAsync(BackendSession session);

    /// <summary>The current health, or null while no Host-bound service exists.</summary>
    Task<HealthStatusSnapshot?> GatherHealthAsync(CancellationToken cancellationToken);

    void InvalidateCatalogs();

    Task<AppSettingsDto?> GetSettingsAsync();

    void SelectCatalogModel(string backend, string model);
}

/// <summary>
/// Resolves the Host-bound services per call: a reconnect replaces the API service and
/// both catalog controllers, so nothing here may hold on to the ones it saw first.
/// </summary>
internal sealed class HostShellBackend(
    IRuBackendSessionProvider provider,
    RuAppComposition composition,
    Func<ApiService?> api,
    Func<HealthStatusService?> health,
    Func<ProviderCatalogController?> catalog,
    Func<GroupedProviderCatalogController?> groupedCatalogs,
    Func<BackendSession, Task> rebind) : IShellBackend
{
    public string DisplayName => provider.DisplayName;

    public bool IsHarness => composition.IsHarness;

    public event EventHandler? BackendExited
    {
        add => provider.BackendExited += value;
        remove => provider.BackendExited -= value;
    }

    public string? ResolveProjectRoot() => composition.IsHarness
        ? composition.HarnessWorkspacePath ?? string.Empty
        : LocateProjectRoot();

    public Task<BackendSession?> StartAsync(string projectRoot, CancellationToken cancellationToken) =>
        provider.StartAsync(projectRoot, cancellationToken);

    public Task RebindAsync(BackendSession session) => rebind(session);

    public async Task<HealthStatusSnapshot?> GatherHealthAsync(CancellationToken cancellationToken) =>
        health() is { } service ? await service.GatherAsync(cancellationToken) : null;

    public void InvalidateCatalogs()
    {
        catalog()?.Invalidate();
        groupedCatalogs()?.Invalidate();
    }

    public Task<AppSettingsDto?> GetSettingsAsync() =>
        api()?.GetSettingsAsync() ?? Task.FromResult<AppSettingsDto?>(null);

    public void SelectCatalogModel(string backend, string model) =>
        groupedCatalogs()?.SetSelection(backend, model);

    private static string? LocateProjectRoot()
    {
        string? envRoot = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot) && File.Exists(Path.Combine(envRoot, "pyproject.toml")))
        {
            return Path.GetFullPath(envRoot);
        }

        var cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "pyproject.toml")))
        {
            return Path.GetFullPath(cwd);
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(candidate, "pyproject.toml")))
        {
            return candidate;
        }

        string? walk = candidate;
        while (walk != null && walk.Length > 3)
        {
            if (File.Exists(Path.Combine(walk, "pyproject.toml")))
            {
                return walk;
            }
            walk = Path.GetDirectoryName(walk);
        }

        // No repository anywhere above us, which is the normal case for an
        // installed copy. Returning null here stopped startup entirely; the
        // directory holding the application is the only root that exists, and the
        // caller picks the actual workspace.
        return RuAppLayout.ResolveWorkingRoot(projectRoot: null, baseDir);
    }
}
