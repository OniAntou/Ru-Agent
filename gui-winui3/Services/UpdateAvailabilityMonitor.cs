using System.Diagnostics;
using RuAgent.Updates;

namespace RuApp.Services;

/// <summary>Owns periodic update checks, their deadline, and their lifetime.</summary>
internal sealed class UpdateAvailabilityMonitor : IDisposable
{
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> _check;
    private readonly Action<Exception> _reportFailure;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _gate = new();
    private Task? _monitorTask;
    private bool _disposed;

    internal UpdateAvailabilityMonitor(
        Func<CancellationToken, Task> check,
        Action<Exception> reportFailure,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _delay = delay ?? Task.Delay;
        _lifetimeToken = _lifetime.Token;
    }

    internal static UpdateChannel ChannelFromSelection(int selectedIndex) =>
        selectedIndex == 1 ? UpdateChannel.Preview : UpdateChannel.Stable;

    internal Task Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _monitorTask ??= MonitorAsync(_lifetimeToken);
        }
    }

    internal async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetimeToken.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;
        if (!_checkGate.Wait(0)) return;

        var releaseGate = true;
        using var checkLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, cancellationToken);
        try
        {
            if (checkLifetime.IsCancellationRequested) return;

            Task check;
            try
            {
                check = _check(checkLifetime.Token);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return;
            }

            Task timeout;
            try
            {
                timeout = _delay(RequestTimeout, deadline.Token);
            }
            catch (Exception error)
            {
                checkLifetime.Cancel();
                releaseGate = false;
                _ = ObserveAndReleaseGateAsync(check, checkLifetime.Token);
                ReportFailure(error);
                return;
            }

            var completed = await Task.WhenAny(check, timeout);
            if (completed == check)
            {
                deadline.Cancel();
                await ObserveDeadlineAsync(timeout);
                try
                {
                    await check;
                }
                catch (OperationCanceledException) when (checkLifetime.IsCancellationRequested)
                {
                    // Stopping the view cancels a well-behaved in-flight request.
                }
                catch (Exception error)
                {
                    ReportFailure(error);
                }
                return;
            }

            if (timeout.IsFaulted)
            {
                checkLifetime.Cancel();
                releaseGate = false;
                _ = ObserveAndReleaseGateAsync(check, checkLifetime.Token);
                ReportFailure(timeout.Exception?.GetBaseException() ?? new InvalidOperationException("The update check deadline failed."));
                return;
            }

            checkLifetime.Cancel();
            releaseGate = false;
            _ = ObserveAndReleaseGateAsync(check, checkLifetime.Token);
            if (!_lifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                ReportFailure(new TimeoutException("The update check exceeded its deadline."));
        }
        finally
        {
            if (releaseGate) _checkGate.Release();
        }
    }

    internal void Stop()
    {
        if (!_lifetimeToken.IsCancellationRequested) _lifetime.Cancel();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckNowAsync(cancellationToken);
                await _delay(AutomaticInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the view ends both the active check and its next interval.
        }
        catch (Exception error)
        {
            ReportFailure(error);
        }
    }

    private async Task ObserveDeadlineAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportFailure(error); }
    }

    private async Task ObserveAndReleaseGateAsync(Task task, CancellationToken cancellationToken)
    {
        try { await task; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(error); }
        finally { _checkGate.Release(); }
    }

    private void ReportFailure(Exception error)
    {
        try { _reportFailure(error); }
        catch (Exception reportingError)
        {
            Trace.TraceError("Update availability failure {0}; reporting failed: {1}", error, reportingError);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        _lifetime.Dispose();
    }
}
