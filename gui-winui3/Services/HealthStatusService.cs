using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

public sealed record HealthStatusSnapshot(
    bool BackendRunning,
    bool WorkerOk,
    string? WorkerError);

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
        bool workerOk = false;
        string? workerError = null;

        if (backendConfigured)
        {
            var (alive, ok, error) = await _apiService.CheckHealthDetailedAsync(cancellationToken);
            backendRunning = alive;
            workerOk = ok;
            workerError = error;
        }

        return new HealthStatusSnapshot(
            backendRunning,
            workerOk,
            workerError);
    }
}
