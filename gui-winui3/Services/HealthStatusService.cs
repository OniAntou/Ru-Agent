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
        bool backendRunning = _backendRunning();
        bool workerOk = false;
        string? workerError = null;
        if (backendRunning)
        {
            (workerOk, workerError) = await _apiService.CheckWorkerHealthAsync(cancellationToken);
        }

        bool vtsSkipped = _isHarness || !workerOk;
        bool vtsConnected = false;
        if (!vtsSkipped)
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
