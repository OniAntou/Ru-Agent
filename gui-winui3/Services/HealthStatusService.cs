using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

public sealed record HealthStatusSnapshot(
    bool BackendRunning,
    bool BackendHealthy,
    string? BackendError);

public sealed class HealthStatusService
{
    private readonly ApiService _apiService;
    private readonly Func<bool> _backendRunning;

    public HealthStatusService(
        ApiService apiService,
        Func<bool> backendRunning)
    {
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _backendRunning = backendRunning ?? throw new ArgumentNullException(nameof(backendRunning));
    }

    public async Task<HealthStatusSnapshot> GatherAsync(CancellationToken cancellationToken)
    {
        bool backendConfigured = _backendRunning();
        bool backendRunning = false;
        bool backendHealthy = false;
        string? backendError = null;

        if (backendConfigured)
        {
            var (alive, ok, error) = await _apiService.CheckHealthDetailedAsync(cancellationToken);
            backendRunning = alive;
            backendHealthy = ok;
            backendError = error;
        }

        return new HealthStatusSnapshot(
            backendRunning,
            backendHealthy,
            backendError);
    }
}
