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
    private readonly CancellationTokenSource _shutdownSignalCts = new();
    private readonly IDiagnosticLogSink _sink;
    private readonly Task _backgroundTask;
    private readonly object _shutdownGate = new();
    private Task<DiagnosticShutdownOutcome>? _shutdownTask;
    private volatile CancellationTokenSource? _shutdownBudgetCts;
    private volatile bool _admissionClosed;
    private bool _disposed;
    private bool _cleanupStarted;
    private CancellationTokenRegistration _shutdownRegistration;
    private readonly TaskCompletionSource<bool> _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeFlushCount;
    private readonly TaskCompletionSource<bool> _flushQuiescedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cleanupMonitorStarted;

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
    }

    public BoundedDiagnosticQueue Queue => _queue;

    public Task Completion => _completionTcs.Task;

    public bool TryEnqueue(DiagnosticEvent evt)
    {
        lock (_shutdownGate)
        {
            if (_admissionClosed || _disposed)
            {
                return false;
            }

            return _queue.TryEnqueue(evt);
        }
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        lock (_shutdownGate)
        {
            if (_admissionClosed || _disposed)
            {
                return;
            }

            _activeFlushCount++;
        }
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

                // Every admitted flush observes the writer-lifetime shutdown signal,
                // including a flush admitted before StopAsync creates its budget.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownSignalCts.Token);
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
                        writeSucceeded = await WriteBatchAsync(batch, linkedCts.Token)
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
            lock (_shutdownGate)
            {
                _activeFlushCount--;
                if (_activeFlushCount == 0 && _admissionClosed)
                {
                    _flushQuiescedTcs.TrySetResult(true);
                }
            }
        }
    }

    public Task<DiagnosticShutdownOutcome> StopAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (budget < TimeSpan.Zero || budget == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        lock (_shutdownGate)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }
        }

        Task<DiagnosticShutdownOutcome> shutdownTask;
        TaskCompletionSource<DiagnosticShutdownOutcome>? shutdownSource = null;
        CancellationTokenSource? consumerCts = null;
        lock (_shutdownGate)
        {
            if (_shutdownTask is not null)
            {
                shutdownTask = _shutdownTask;
            }
            else
            {
                _admissionClosed = true;
                shutdownSource = new TaskCompletionSource<DiagnosticShutdownOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                shutdownTask = shutdownSource.Task;
                _shutdownTask = shutdownTask;
                if (_activeFlushCount == 0)
                {
                    _flushQuiescedTcs.TrySetResult(true);
                }

                consumerCts = _cts;
            }
        }

        if (shutdownSource is null) return shutdownTask;

        _ = StartShutdownAsync(budget, cancellationToken, shutdownSource);

        // Cancellation callbacks may execute arbitrary continuations. Keep them
        // outside the admission/ownership lock.
        try { consumerCts?.Cancel(); } catch (ObjectDisposedException) { }
        return shutdownTask;
    }

    private async Task StartShutdownAsync(
        TimeSpan budget,
        CancellationToken userToken,
        TaskCompletionSource<DiagnosticShutdownOutcome> shutdownSource)
    {
        CancellationTokenSource? budgetCts = null;
        CancellationTokenRegistration shutdownRegistration = default;
        try
        {
            budgetCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            budgetCts.CancelAfter(budget);
            shutdownRegistration = budgetCts.Token.Register(
                static state =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                },
                _shutdownSignalCts);

            lock (_shutdownGate)
            {
                _shutdownBudgetCts = budgetCts;
                _shutdownRegistration = shutdownRegistration;
                EnsureCleanupMonitorStarted();
            }

            var outcome = await ShutdownAsync(budget, budgetCts, userToken).ConfigureAwait(false);
            shutdownSource.TrySetResult(outcome);
        }
        catch
        {
            shutdownSource.TrySetResult(DiagnosticShutdownOutcome.Incomplete);
        }
    }

    private async Task<DiagnosticShutdownOutcome> ShutdownAsync(TimeSpan budget, CancellationTokenSource budgetCts, CancellationToken userToken)
    {
        Task quiescence = Task.WhenAll(_backgroundTask, _flushQuiescedTcs.Task);
        try
        {
            await quiescence.WaitAsync(budget, userToken).ConfigureAwait(false);

            var snapshot = _queue.GetSnapshot();
            DisposeGatesAndComplete();
            return snapshot.QueueDepth == 0
                ? DiagnosticShutdownOutcome.Drained
                : DiagnosticShutdownOutcome.Cancelled;
        }
        catch (TimeoutException)
        {
            return DiagnosticShutdownOutcome.Incomplete;
        }
        catch (OperationCanceledException)
        {
            return _backgroundTask.IsCompleted && _flushQuiescedTcs.Task.IsCompleted
                ? DiagnosticShutdownOutcome.Cancelled
                : DiagnosticShutdownOutcome.Incomplete;
        }
        catch
        {
            return DiagnosticShutdownOutcome.Incomplete;
        }
    }

    private void EnsureCleanupMonitorStarted()
    {
        if (_cleanupMonitorStarted)
        {
            return;
        }

        _cleanupMonitorStarted = true;
        _ = CompleteWhenQuiescentAsync();
    }

    private async Task CompleteWhenQuiescentAsync()
    {
        try
        {
            await Task.WhenAll(_backgroundTask, _flushQuiescedTcs.Task).ConfigureAwait(false);
        }
        catch
        {
            // The ownership boundary still requires both tasks to have ended before
            // synchronization primitives can be released.
            try { await _backgroundTask.ConfigureAwait(false); } catch { }
            try { await _flushQuiescedTcs.Task.ConfigureAwait(false); } catch { }
        }

        DisposeGatesAndComplete();
    }

    private void DisposeGatesAndComplete()
    {
        CancellationTokenRegistration shutdownRegistration;
        CancellationTokenSource? budgetCts;
        lock (_shutdownGate)
        {
            if (_cleanupStarted) return;
            _cleanupStarted = true;
            shutdownRegistration = _shutdownRegistration;
            _shutdownRegistration = default;
            budgetCts = _shutdownBudgetCts;
            _shutdownBudgetCts = null;
        }

        // Resource disposal, including registration disposal, is outside the
        // ownership lock. No admitted operation remains at this point.
        try { shutdownRegistration.Dispose(); } catch { }
        try { budgetCts?.Dispose(); } catch { }
        try { _shutdownSignalCts.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _batchGate.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
        _completionTcs.TrySetResult(true);
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
        lock (_shutdownGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

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
        // Incomplete shutdown owns cleanup through CompleteWhenQuiescentAsync.
    }
}
