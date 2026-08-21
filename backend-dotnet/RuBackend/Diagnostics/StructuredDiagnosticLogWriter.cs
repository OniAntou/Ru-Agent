namespace RuBackend.Diagnostics;

using System.Text;

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
    private readonly Task _backgroundTask;
    private bool _disposed;

    public StructuredDiagnosticLogWriter(
        string logDirectory,
        string prefix = "aspnetcore-",
        BoundedDiagnosticQueue? queue = null,
        int batchSize = 64,
        TimeSpan? flushInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = logDirectory;
        _prefix = prefix;
        _queue = queue ?? new BoundedDiagnosticQueue();
        _batchSize = Math.Clamp(batchSize, 1, 512);
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(200);

        _backgroundTask = Task.Run(ProcessQueueAsync);
    }

    public BoundedDiagnosticQueue Queue => _queue;

    public bool TryEnqueue(DiagnosticEvent evt)
    {
        if (_disposed)
        {
            return false;
        }

        return _queue.TryEnqueue(evt);
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = DateTime.UtcNow + timeout;
        var batch = new List<DiagnosticEvent>(_batchSize);

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero ||
                !await _batchGate.WaitAsync(remaining).ConfigureAwait(false))
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
                    writeSucceeded = await WriteBatchAsync(batch, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _batchGate.Release();
            }

            if (count == 0)
            {
                // Holding the batch gate proves that no background write is
                // still draining the queue.
                break;
            }

            if (!writeSucceeded && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
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
                await _batchGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    batch.Clear();
                    count = _queue.DrainBatch(batch, _batchSize);
                    if (count > 0)
                    {
                        writeSucceeded = await WriteBatchAsync(batch, _cts.Token)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    _batchGate.Release();
                }

                if (count == 0 || !writeSucceeded)
                {
                    await Task.Delay(_flushInterval, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                _queue.RecordWriteFailure();
            }
        }

        // Drain remaining on shutdown
        try
        {
            while (true)
            {
                var count = 0;
                var writeSucceeded = true;
                await _batchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    batch.Clear();
                    count = _queue.DrainBatch(batch, _batchSize);
                    if (count > 0)
                    {
                        writeSucceeded = await WriteBatchAsync(batch, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    _batchGate.Release();
                }

                if (count == 0 || !writeSucceeded)
                {
                    break;
                }
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
                await using var stream = new FileStream(
                    filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);

                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                _writeLock.Release();
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
        _cts.Cancel();

        try
        {
            _backgroundTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        _cts.Dispose();
        _batchGate.Dispose();
        _writeLock.Dispose();
    }
}
