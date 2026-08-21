using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticLogWriterTests : IDisposable
{
    private readonly string _tempLogDir;

    public DiagnosticLogWriterTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "ru-diag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLogDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Writer_enqueues_non_blocking_and_flushes_batches_to_daily_jsonl()
    {
        var queue = new BoundedDiagnosticQueue(capacity: 100);
        using var writer = new StructuredDiagnosticLogWriter(
            _tempLogDir,
            prefix: "aspnetcore-",
            queue: queue,
            batchSize: 64,
            flushInterval: TimeSpan.FromMilliseconds(50));

        var evt1 = new DiagnosticEvent(
            EventId: "evt_1",
            Ts: 1720000000.1,
            Iso: "2026-08-20T00:00:00.100Z",
            Level: "info",
            Component: "host",
            EventCode: "host_startup");

        var evt2 = new DiagnosticEvent(
            EventId: "evt_2",
            Ts: 1720000000.2,
            Iso: "2026-08-20T00:00:00.200Z",
            Level: "warning",
            Component: "host.ws",
            EventCode: "worker_unavailable");

        Assert.True(queue.TryEnqueue(evt1));
        Assert.True(queue.TryEnqueue(evt2));

        await writer.FlushAsync(TimeSpan.FromSeconds(2));

        var files = Directory.GetFiles(_tempLogDir, "aspnetcore-*.jsonl");
        Assert.Single(files);

        var lines = await File.ReadAllLinesAsync(files[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("host_startup", lines[0]);
        Assert.Contains("worker_unavailable", lines[1]);
    }

    [Fact]
    public void Queue_drops_lower_severity_when_full_and_increments_drop_counter()
    {
        var queue = new BoundedDiagnosticQueue(capacity: 3);

        var info1 = new DiagnosticEvent("e1", 1, "2026-08-20T00:00:00Z", "info", "host", "c1");
        var info2 = new DiagnosticEvent("e2", 2, "2026-08-20T00:00:00Z", "debug", "host", "c2");
        var info3 = new DiagnosticEvent("e3", 3, "2026-08-20T00:00:00Z", "trace", "host", "c3");

        Assert.True(queue.TryEnqueue(info1));
        Assert.True(queue.TryEnqueue(info2));
        Assert.True(queue.TryEnqueue(info3));

        // Adding an error should drop the lowest severity item
        var error = new DiagnosticEvent("e4", 4, "2026-08-20T00:00:00Z", "error", "host", "c4");
        Assert.True(queue.TryEnqueue(error));

        var snapshot = queue.GetSnapshot();
        Assert.True(snapshot.DroppedCount >= 1);
        Assert.Equal(3, snapshot.QueueDepth);
    }

    [Fact]
    public void Queue_can_requeue_a_failed_batch_without_losing_order()
    {
        var queue = new BoundedDiagnosticQueue(capacity: 4);
        var first = new DiagnosticEvent("e1", 1, "2026-08-20T00:00:00Z", "info", "host", "c1");
        var second = new DiagnosticEvent("e2", 2, "2026-08-20T00:00:00Z", "info", "host", "c2");

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));
        var batch = new List<DiagnosticEvent>();
        Assert.Equal(2, queue.DrainBatch(batch, 2));

        queue.RequeueFront(batch);

        var retry = new List<DiagnosticEvent>();
        Assert.Equal(2, queue.DrainBatch(retry, 2));
        Assert.Equal(new[] { "e1", "e2" }, retry.Select(evt => evt.EventId));
    }
}
