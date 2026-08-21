using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

/// <summary>
/// Bounded, privacy-safe diagnostic writer for the desktop process.
/// Enqueueing never serializes exception text and never performs file I/O.
/// </summary>
public sealed class GuiDiagnosticWriter : IDisposable
{
    private const int MaxBatchSize = 64;
    private readonly object _gate = new();
    private readonly Queue<GuiDiagnosticEvent> _queue = new();
    private readonly int _capacity;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _drained = new(true);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private bool _closed;
    private int _droppedCount;
    private int _writeFailureCount;

    public GuiDiagnosticWriter(string? logRoot = null, int capacity = 256)
    {
        _capacity = Math.Max(1, capacity);
        LogRoot = ResolveLogRoot(logRoot);
        _worker = Task.Factory.StartNew(
            WorkerLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public string LogRoot { get; }

    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedCount;
            }
        }
    }

    public int WriteFailureCount
    {
        get
        {
            lock (_gate)
            {
                return _writeFailureCount;
            }
        }
    }

    public bool TryEnqueueCrash(Exception? exception)
    {
        var diagnosticEvent = new GuiDiagnosticEvent(
            EventId: $"evt_{Guid.NewGuid():N}",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            Iso: DateTimeOffset.UtcNow.ToString("O"),
            Level: "critical",
            Component: "gui",
            EventCode: "gui_crash",
            Status: "failed",
            ErrorCode: "gui_exception",
            ExceptionKind: ClassifyException(exception));

        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                _droppedCount++;
            }

            _queue.Enqueue(diagnosticEvent);
            _drained.Reset();
        }

        _signal.Set();
        return true;
    }

    public bool Flush(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _signal.Set();
        return _drained.Wait(timeout);
    }

    public static bool WriteCrashEvent(Exception? exception, string? logRoot = null)
    {
        try
        {
            using var writer = new GuiDiagnosticWriter(logRoot, capacity: 1);
            return writer.TryEnqueueCrash(exception)
                && writer.Flush(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Diagnostics must never replace the original crash path.
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        Flush(TimeSpan.FromSeconds(2));
        _stop.Cancel();
        _signal.Set();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // A diagnostics shutdown failure must not affect app shutdown.
        }

        _signal.Dispose();
        _drained.Dispose();
        _stop.Dispose();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _signal.WaitOne(250);

            while (true)
            {
                var batch = DequeueBatch();
                if (batch.Count == 0)
                {
                    break;
                }

                if (!WriteBatch(batch))
                {
                    // Keep failures bounded and avoid a hot retry loop when the
                    // log directory or disk is temporarily unavailable.
                    Thread.Sleep(100);
                    break;
                }
            }

            bool shouldStop;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _drained.Set();
                }

                shouldStop = _stop.IsCancellationRequested && _queue.Count == 0;
            }

            if (shouldStop)
            {
                return;
            }
        }
    }

    private List<GuiDiagnosticEvent> DequeueBatch()
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return [];
            }

            var batch = new List<GuiDiagnosticEvent>(Math.Min(MaxBatchSize, _queue.Count));
            while (batch.Count < MaxBatchSize && _queue.Count > 0)
            {
                batch.Add(_queue.Dequeue());
            }

            return batch;
        }
    }

    private bool WriteBatch(IReadOnlyList<GuiDiagnosticEvent> batch)
    {
        var written = 0;
        try
        {
            Directory.CreateDirectory(LogRoot);
            foreach (var diagnosticEvent in batch)
            {
                var date = DateTimeOffset.FromUnixTimeMilliseconds(
                    checked((long)(diagnosticEvent.Timestamp * 1000)))
                    .UtcDateTime
                    .ToString("yyyy-MM-dd");
                var path = Path.Combine(LogRoot, $"gui-{date}.jsonl");
                var line = JsonSerializer.Serialize(diagnosticEvent, _jsonOptions)
                    + Environment.NewLine;
                File.AppendAllText(path, line);
                written++;
            }

            return true;
        }
        catch
        {
            lock (_gate)
            {
                _writeFailureCount++;
                for (var index = batch.Count - 1; index >= written; index--)
                {
                    if (_queue.Count >= _capacity)
                    {
                        _queue.Dequeue();
                        _droppedCount++;
                    }

                    var pending = batch[index];
                    var reordered = _queue.ToList();
                    _queue.Clear();
                    _queue.Enqueue(pending);
                    foreach (var queued in reordered)
                    {
                        _queue.Enqueue(queued);
                    }
                }

                _drained.Reset();
            }

            return false;
        }
    }

    private static string ResolveLogRoot(string? explicitRoot)
    {
        var configured = !string.IsNullOrWhiteSpace(explicitRoot)
            ? explicitRoot
            : Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, "Ru", "logs");
    }

    private static string ClassifyException(Exception? exception)
    {
        return exception switch
        {
            OperationCanceledException => "cancellation",
            TimeoutException => "timeout",
            UnauthorizedAccessException => "permission",
            IOException => "io",
            System.Net.Http.HttpRequestException => "network",
            System.Net.WebSockets.WebSocketException => "network",
            _ => "unknown",
        };
    }

    private sealed record GuiDiagnosticEvent(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("ts")] double Timestamp,
        [property: JsonPropertyName("iso")] string Iso,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("component")] string Component,
        [property: JsonPropertyName("event_code")] string EventCode,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("exception_kind")] string ExceptionKind);
}
