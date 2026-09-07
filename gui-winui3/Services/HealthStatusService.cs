using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

public sealed record HealthStatusSnapshot(
    bool BackendRunning,
    bool WorkerOk,
    string? WorkerError,
    bool VtsConnected,
    bool VtsSkipped);

public sealed class HealthStatusService
{
    private readonly ApiService _apiService;
    private readonly Func<bool> _backendRunning;
    private readonly bool _isHarness;

    public HealthStatusService(
        ApiService apiService,
        Func<bool> backendRunning,
        bool isHarness)
    {
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _backendRunning = backendRunning ?? throw new ArgumentNullException(nameof(backendRunning));
        _isHarness = isHarness;
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

        bool vtsSkipped = _isHarness || !workerOk;
        bool vtsConnected = false;
        if (!vtsSkipped && backendRunning)
        {
            var vtsStatus = await _apiService.GetVtsStatusAsync(cancellationToken);
            vtsConnected = vtsStatus?.Connected == true;
        }

        return new HealthStatusSnapshot(
            backendRunning,
            workerOk,
            workerError,
            vtsConnected,
            _isHarness);
    }
}
