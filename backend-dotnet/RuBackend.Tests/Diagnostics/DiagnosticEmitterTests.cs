using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticEmitterTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(),
        "ru-diagnostic-emitter-" + Guid.NewGuid().ToString("N"));

    public DiagnosticEmitterTests()
    {
        Directory.CreateDirectory(_logDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TryEmit_enqueues_only_sanitized_scalar_fields()
    {
        using var writer = new StructuredDiagnosticLogWriter(
            _logDirectory,
            prefix: "aspnetcore-",
            flushInterval: TimeSpan.FromSeconds(10));
        var emitter = new DiagnosticEmitter(writer, TimeProvider.System);

        Assert.True(emitter.TryEmit(
            "ERROR",
            "host.with spaces",
            "operation failed!",
            "bad correlation with Bearer secret",
            durationMs: 4.567,
            status: "FAILED",
            errorCode: "network timeout",
            exceptionKind: "System.InvalidOperationException"));

        var snapshot = writer.Queue.GetSnapshot();
        Assert.Equal(1, snapshot.EnqueuedCount);

        await writer.FlushAsync(TimeSpan.FromSeconds(2));
        var file = Assert.Single(Directory.GetFiles(_logDirectory, "aspnetcore-*.jsonl"));
        var parsed = DiagnosticEventPolicy.Deserialize((await File.ReadAllLinesAsync(file))[0]);

        Assert.NotNull(parsed);
        Assert.Equal("error", parsed.Level);
        Assert.Equal("host", parsed.Component);
        Assert.Equal("diagnostic_unclassified", parsed.EventCode);
        Assert.StartsWith("c_", parsed.CorrelationId);
        Assert.Equal(4.57, parsed.DurationMs);
        Assert.Equal("failed", parsed.Status);
        Assert.Null(parsed.ErrorCode);
        Assert.Equal("unknown", parsed.ExceptionKind);
    }
}
