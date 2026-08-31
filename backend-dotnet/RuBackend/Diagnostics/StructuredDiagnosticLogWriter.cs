namespace RuBackend.Diagnostics;

using System.Text;

public enum DiagnosticShutdownOutcome { Drained, Cancelled, Incomplete }

public sealed class StructuredDiagnosticLogWriter : IDisposable
{
    private readonly string _logDirectory;
    private readonly string _prefix;
    private readonly BoundedDiagnosticQueue _queue;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IDiagnosticLogSink _sink;
    private readonly Task _backgroundTask;
    private readonly object _shutdownGate = new();
    private Task<DiagnosticShutdownOutcome>? _shutdownTask;
    private volatile CancellationTokenSource? _shutdownBudgetCts;
    private volatile bool _admissionClosed;
    private bool _disposed;
    private readonly TaskCompletionSource<bool> _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeFlushCount;
    private readonly object _flushGate = new();

    public StructuredDiagnosticLogWriter(
        string logDirectory,
        string prefix = "aspnetcore-",
        BoundedDiagnosticQueue? queue = null,
        int batchSize = 64,
        TimeSpan? flushInterval = null,
        IDiagnosticLogSink? sink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = logDirectory;
        _prefix = prefix;
        _queue = queue ?? new BoundedDiagnosticQueue();
        _batchSize = Math.Clamp(batchSize, 1, 512);
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(200);
        _sink = sink ?? new JsonlDiagnosticLogSink();

        _backgroundTask = Task.Run(ProcessQueueAsync);
        // Ensure Completion completes even if background exits normally without shutdown
        _backgroundTask.ContinueWith(_ => TrySetCompletion(), TaskScheduler.Default);
    }

    public BoundedDiagnosticQueue Queue => _queue;

    public Task Completion => _completionTcs.Task;

    public bool TryEnqueue(DiagnosticEvent evt)
    {
        if (_admissionClosed || _disposed)
        {
            return false;
        }

        return _queue.TryEnqueue(evt);
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        if (_admissionClosed || _disposed)
        {
            return;
        }

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_flushGate) { _activeFlushCount++; }
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            var batch = new List<DiagnosticEvent>(_batchSize);

            while (DateTime.UtcNow < deadline)
            {
                if (_admissionClosed) break;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                // Respect shutdown budget cancellation if active
                var shutdownToken = _shutdownBudgetCts?.Token ?? CancellationToken.None;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                linkedCts.CancelAfter(remaining);
                bool gateTaken = false;
                try
                {
                    await _batchGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    gateTaken = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var count = 0;
                var writeSucceeded = true;
                try
                {
                    batch.Clear();
                    count = _queue.DrainBatch(batch, _batchSize);
                    if (count > 0)
                    {
                        // Use shutdown token if present, otherwise linked remaining token
                        var writeToken = _shutdownBudgetCts?.Token ?? linkedCts.Token;
                        writeSucceeded = await WriteBatchAsync(batch, writeToken)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (gateTaken) _batchGate.Release();
                }

                if (count == 0)
                {
                    break;
                }

                if (!writeSucceeded && DateTime.UtcNow < deadline)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(50), linkedCts.Token).ConfigureAwait(false); } catch { break; }
                }

                if (_admissionClosed) break;
            }
        }
        finally
        {
            lock (_flushGate)
            {
                _activeFlushCount--;
                if (_activeFlushCount == 0 && _admissionClosed)
                {
                    // If shutdown is waiting for flush quiescence, it will poll via _backgroundTask + flush count
                }
            }
        }
    }

    public Task<DiagnosticShutdownOutcome> StopAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        lock (_shutdownGate)
        {
            if (_shutdownTask != null)
            {
                return _shutdownTask;
            }

            _admissionClosed = true;
            var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(budget);
            _shutdownBudgetCts = budgetCts;
            try { _cts.Cancel(); } catch { }

            _shutdownTask = ShutdownAsync(budget, budgetCts, cancellationToken);
            // Ensure budgetCts disposed after shutdown task completes
            _shutdownTask.ContinueWith(_ => { try { budgetCts.Dispose(); } catch { } }, TaskScheduler.Default);
            return _shutdownTask;
        }
    }

    private async Task<DiagnosticShutdownOutcome> ShutdownAsync(TimeSpan budget, CancellationTokenSource budgetCts, CancellationToken userToken)
    {
        var outcome = DiagnosticShutdownOutcome.Cancelled;
        var budgetToken = budgetCts.Token;

        // Wait for background task with budget timeout, also wait for active flushes to quiesce
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Wait for background task to complete within budget (plus small grace for cancellation handling)
            var waitBudget = budget + TimeSpan.FromMilliseconds(500);
            try
            {
                await _backgroundTask.WaitAsync(waitBudget, userToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!_backgroundTask.IsCompleted)
                {
                    // Do not dispose gates yet; attach continuation to dispose when finally completes
                    _ = _backgroundTask.ContinueWith(_ => DisposeGatesAndComplete(), TaskScheduler.Default);
                    return DiagnosticShutdownOutcome.Incomplete;
                }
            }

            // Background completed within budget, now wait for flush quiescence (activeFlushCount == 0) within remaining budget
            var remaining = budget - sw.Elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var flushWaitStart = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                int active;
                lock (_flushGate) { active = _activeFlushCount; }
                if (active == 0) break;
                if (flushWaitStart.Elapsed >= remaining) break;
                try { await Task.Delay(10, budgetToken).ConfigureAwait(false); } catch { break; }
            }

            lock (_flushGate)
            {
                if (_activeFlushCount != 0)
                {
                    _backgroundTask.ContinueWith(_ => DisposeGatesAndComplete(), TaskScheduler.Default);
                    return DiagnosticShutdownOutcome.Incomplete;
                }
            }

            // Determine Drained vs Cancelled: if queue is empty and no write failures due to cancellation, consider Drained
            var snapshot = _queue.GetSnapshot();
            // If we were cancelled (budget token triggered) and queue still has items that were requeued, it's Cancelled
            // For cooperative sink, queue should be empty
            if (snapshot.QueueDepth == 0)
            {
                outcome = DiagnosticShutdownOutcome.Drained;
            }
            else
            {
                outcome = DiagnosticShutdownOutcome.Cancelled;
            }

            // Check if cancellation was requested
            if (budgetToken.IsCancellationRequested || userToken.IsCancellationRequested)
            {
                // If we completed draining despite cancellation, still consider Cancelled if not fully drained
                if (outcome == DiagnosticShutdownOutcome.Drained && (budgetToken.IsCancellationRequested))
                {
                    // If we drained fully before cancellation fired, keep Drained; otherwise Cancelled
                    // We already set Drained when empty, but if cancellation fired we were still empty, keep Drained
                }
            }

            DisposeGatesAndComplete();
            return outcome;
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            // User cancellation -> treat as Cancelled if quiescent else Incomplete
            if (_backgroundTask.IsCompleted)
            {
                int active;
                lock (_flushGate) { active = _activeFlushCount; }
                if (active == 0)
                {
                    DisposeGatesAndComplete();
                    return DiagnosticShutdownOutcome.Cancelled;
                }
            }
            _ = _backgroundTask.ContinueWith(_ => DisposeGatesAndComplete(), TaskScheduler.Default);
            return DiagnosticShutdownOutcome.Incomplete;
        }
        catch
        {
            DisposeGatesAndComplete();
            return DiagnosticShutdownOutcome.Cancelled;
        }
    }

    private void DisposeGatesAndComplete()
    {
        lock (_shutdownGate)
        {
            if (_completionTcs.Task.IsCompleted) return;
            try { _cts.Dispose(); } catch { }
            try { _batchGate.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
            _completionTcs.TrySetResult(true);
        }
    }

    private void TrySetCompletion()
    {
        // Called when background task exits without explicit shutdown (e.g., process exit)
        // Only complete if admission already closed; otherwise background exiting unexpectedly should still complete?
        // For normal Dispose path, ShutdownAsync will handle.
        // If background exits spontaneously, we still want to signal completion but not dispose gates prematurely if shutdown not started?
        // No-op if shutdown already handling.
        lock (_shutdownGate)
        {
            if (_completionTcs.Task.IsCompleted) return;
            if (_shutdownTask != null) return; // shutdown will dispose
            // If no shutdown, background exiting means we should complete but not dispose gates twice
            _completionTcs.TrySetResult(true);
        }
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<DiagnosticEvent>(_batchSize);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var count = 0;
                var writeSucceeded = true;
                // Use _cts.Token for main loop; if shutdown budget cancels, this will throw and exit loop
                var gateToken = _shutdownBudgetCts?.Token ?? _cts.Token;
                // Need to handle case where _shutdownBudgetCts created after loop started: we check each iteration
                await _batchGate.WaitAsync(gateToken).ConfigureAwait(false);
                try
                {
                    batch.Clear();
                    count = _queue.DrainBatch(batch, _batchSize);
                    if (count > 0)
                    {
                        var writeToken = _shutdownBudgetCts?.Token ?? _cts.Token;
                        writeSucceeded = await WriteBatchAsync(batch, writeToken)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    _batchGate.Release();
                }

                if (count == 0 || !writeSucceeded)
                {
                    try { await Task.Delay(_flushInterval, gateToken).ConfigureAwait(false); } catch { break; }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested || (_shutdownBudgetCts?.IsCancellationRequested ?? false))
            {
                break;
            }
            catch
            {
                _queue.RecordWriteFailure();
            }
        }

        // Drain remaining on shutdown - cancellable with shutdown budget token
        try
        {
            while (true)
            {
                if (_shutdownBudgetCts?.IsCancellationRequested ?? false)
                {
                    break;
                }

                var count = 0;
                var writeSucceeded = true;
                var drainToken = _shutdownBudgetCts?.Token ?? CancellationToken.None;
                // Try to acquire gate with drainToken (if cancelled, exit)
                try
                {
                    await _batchGate.WaitAsync(drainToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    batch.Clear();
                    count = _queue.DrainBatch(batch, _batchSize);
                    if (count > 0)
                    {
                        writeSucceeded = await WriteBatchAsync(batch, drainToken)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    try { _batchGate.Release(); } catch { }
                }

                if (count == 0 || !writeSucceeded)
                {
                    break;
                }

                if (_shutdownBudgetCts?.IsCancellationRequested ?? false) break;
            }
        }
        catch
        {
            _queue.RecordWriteFailure();
        }
    }

    private async Task<bool> WriteBatchAsync(List<DiagnosticEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return true;
        }

        var lockTaken = false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            Directory.CreateDirectory(_logDirectory);

            var groups = batch.GroupBy(e =>
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.Ts * 1000)).UtcDateTime;
                return dt.ToString("yyyy-MM-dd");
            });

            foreach (var group in groups)
            {
                var filePath = Path.Combine(_logDirectory, $"{_prefix}{group.Key}.jsonl");
                var sb = new StringBuilder();
                foreach (var evt in group)
                {
                    sb.AppendLine(DiagnosticEventPolicy.Serialize(evt));
                }

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                await _sink.AppendAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _queue.RequeueFront(batch);
            return false;
        }
        catch
        {
            _queue.RecordWriteFailure();
            _queue.RequeueFront(batch);
            return false;
        }
        finally
        {
            if (lockTaken)
            {
                try { _writeLock.Release(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var task = StopAsync(TimeSpan.FromSeconds(2));
        try
        {
            // Wait synchronously with timeout; if Incomplete, do not throw, just return and let continuation handle cleanup
            task.GetAwaiter().GetResult();
        }
        catch { }

        // If shutdown returned Incomplete, gates are not disposed yet; Completion will dispose later
        // If Drained/Cancelled, gates already disposed via DisposeGatesAndComplete
        // For safety, if task completed with Drained/Cancelled but completion not yet set, ensure dispose
        if (task.IsCompletedSuccessfully)
        {
            var outcome = task.Result;
            if (outcome != DiagnosticShutdownOutcome.Incomplete)
            {
                // Already disposed; ensure completion is set
                if (!_completionTcs.Task.IsCompleted)
                {
                    DisposeGatesAndComplete();
                }
            }
            else
            {
                // Incomplete: do not dispose now, wait for background to finish
                // But we already attached continuation in ShutdownAsync
            }
        }
        else
        {
            // Task faulted/cancelled - ensure eventual cleanup
            _backgroundTask.ContinueWith(_ => DisposeGatesAndComplete(), TaskScheduler.Default);
        }
    }
}
