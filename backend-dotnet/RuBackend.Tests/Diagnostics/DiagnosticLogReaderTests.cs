using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticLogReaderTests : IDisposable
{
    private readonly string _tempLogDir;

    public DiagnosticLogReaderTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "ru-diag-reader-test-" + Guid.NewGuid().ToString("N"));
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
    public async Task Reader_merges_aspnetcore_worker_gui_files_deterministically_and_skips_malformed()
    {
        var aspnetFile = Path.Combine(_tempLogDir, "aspnetcore-2026-08-20.jsonl");
        var workerFile = Path.Combine(_tempLogDir, "worker-2026-08-20.jsonl");
        var guiFile = Path.Combine(_tempLogDir, "gui-2026-08-20.jsonl");
        var ignoredFile = Path.Combine(_tempLogDir, "activity_log.jsonl");

        await File.WriteAllLinesAsync(aspnetFile, new[]
        {
            "{\"event_id\":\"evt_1\",\"ts\":100.0,\"iso\":\"2026-08-20T00:01:40.000Z\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"c1\"}",
            "{ malformed json line here ...",
            "{\"event_id\":\"evt_3\",\"ts\":300.0,\"iso\":\"2026-08-20T00:05:00.000Z\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"c3\"}"
        });

        await File.WriteAllLinesAsync(workerFile, new[]
        {
            "{\"event_id\":\"evt_2\",\"ts\":200.0,\"iso\":\"2026-08-20T00:03:20.000Z\",\"level\":\"warning\",\"component\":\"worker\",\"event_code\":\"c2\"}"
        });

        await File.WriteAllLinesAsync(guiFile, new[]
        {
            "{\"event_id\":\"evt_0\",\"ts\":50.0,\"iso\":\"2026-08-20T00:00:50.000Z\",\"level\":\"info\",\"component\":\"gui\",\"event_code\":\"c0\"}"
        });

        await File.WriteAllLinesAsync(ignoredFile, new[]
        {
            "{\"event_id\":\"evt_ignored\",\"ts\":10.0,\"iso\":\"2026-08-20T00:00:10.000Z\",\"type\":\"chat\",\"data\":{}}"
        });

        var reader = new DiagnosticLogReader(_tempLogDir);
        var result = await reader.ReadLogsAsync(new DiagnosticLogQuery(Limit: 100));

        Assert.Equal(4, result.Events.Count);
        Assert.Equal("evt_0", result.Events[0].EventId);
        Assert.Equal("evt_1", result.Events[1].EventId);
        Assert.Equal("evt_2", result.Events[2].EventId);
        Assert.Equal("evt_3", result.Events[3].EventId);
        Assert.NotNull(result.Cursor);
    }

    [Fact]
    public async Task Reader_filters_by_level_component_and_correlation_id()
    {
        var aspnetFile = Path.Combine(_tempLogDir, "aspnetcore-2026-08-20.jsonl");
        await File.WriteAllLinesAsync(aspnetFile, new[]
        {
            "{\"event_id\":\"evt_1\",\"ts\":100.0,\"iso\":\"2026-08-20T00:01:40.000Z\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"c1\",\"correlation_id\":\"c_12345678\"}",
            "{\"event_id\":\"evt_2\",\"ts\":200.0,\"iso\":\"2026-08-20T00:03:20.000Z\",\"level\":\"error\",\"component\":\"worker.voice\",\"event_code\":\"c2\",\"correlation_id\":\"c_12345678\"}",
            "{\"event_id\":\"evt_3\",\"ts\":300.0,\"iso\":\"2026-08-20T00:05:00.000Z\",\"level\":\"error\",\"component\":\"host\",\"event_code\":\"c3\",\"correlation_id\":\"c_87654321\"}"
        });

        var reader = new DiagnosticLogReader(_tempLogDir);

        var levelFiltered = await reader.ReadLogsAsync(new DiagnosticLogQuery(Level: "error"));
        Assert.Equal(2, levelFiltered.Events.Count);

        var compFiltered = await reader.ReadLogsAsync(new DiagnosticLogQuery(Component: "worker.voice"));
        Assert.Single(compFiltered.Events);
        Assert.Equal("evt_2", compFiltered.Events[0].EventId);

        var corrFiltered = await reader.ReadLogsAsync(new DiagnosticLogQuery(CorrelationId: "c_12345678"));
        Assert.Equal(2, corrFiltered.Events.Count);
    }

    [Theory]
    [InlineData("not-a-date", "diagnostic_since_invalid")]
    [InlineData("2026-08-20T00:00:00Z", "diagnostic_cursor_invalid")]
    public async Task Reader_rejects_invalid_since_or_cursor(string value, string errorCode)
    {
        var reader = new DiagnosticLogReader(_tempLogDir);
        var query = errorCode == "diagnostic_since_invalid"
            ? new DiagnosticLogQuery(Since: value)
            : new DiagnosticLogQuery(Cursor: value);

        var error = await Assert.ThrowsAsync<DiagnosticQueryValidationException>(
            () => reader.ReadLogsAsync(query));

        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Fact]
    public async Task Reader_rejects_limits_and_filters_outside_the_bounded_contract()
    {
        var reader = new DiagnosticLogReader(_tempLogDir);

        var limitError = await Assert.ThrowsAsync<DiagnosticQueryValidationException>(
            () => reader.ReadLogsAsync(new DiagnosticLogQuery(Limit: DiagnosticLogQueryPolicy.MaxQueryLimit + 1)));
        Assert.Equal("diagnostic_limit_out_of_range", limitError.ErrorCode);

        var filterError = await Assert.ThrowsAsync<DiagnosticQueryValidationException>(
            () => reader.ReadLogsAsync(new DiagnosticLogQuery(Component: new string('x', DiagnosticLogQueryPolicy.MaxFilterLength + 1))));
        Assert.Equal("diagnostic_filter_too_long", filterError.ErrorCode);
    }

    [Fact]
    public async Task Reader_skips_oversized_records_and_keeps_scan_bounded()
    {
        var logFile = Path.Combine(_tempLogDir, "aspnetcore-2026-08-20.jsonl");
        await File.WriteAllLinesAsync(logFile, new[]
        {
            "{\"event_id\":\"huge\",\"ts\":1,\"iso\":\"2026-08-20T00:00:00Z\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"huge\",\"padding\":\"" + new string('x', DiagnosticLogQueryPolicy.MaxRecordBytes) + "\"}",
            "{\"event_id\":\"small\",\"ts\":2,\"iso\":\"2026-08-20T00:00:01Z\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"small\"}",
        });

        var reader = new DiagnosticLogReader(_tempLogDir);
        var result = await reader.ReadLogsAsync(new DiagnosticLogQuery(Limit: 10));

        var actual = Assert.Single(result.Events);
        Assert.Equal("small", actual.EventId);
    }

    [Fact]
    public async Task Reader_skips_a_giant_unterminated_record_without_unbounded_line_allocation()
    {
        var logFile = Path.Combine(_tempLogDir, "aspnetcore-2026-08-20.jsonl");
        await File.WriteAllTextAsync(logFile,
            new string('x', DiagnosticLogQueryPolicy.MaxRecordBytes * 2) +
            "\n{\"event_id\":\"small\",\"ts\":2,\"iso\":\"2026-08-20T00:00:01Z\",\"level\":\"info\",\"component\":\"host\",\"event_code\":\"small\"}");

        var result = await new DiagnosticLogReader(_tempLogDir)
            .ReadLogsAsync(new DiagnosticLogQuery(Limit: 10));

        var actual = Assert.Single(result.Events);
        Assert.Equal("small", actual.EventId);
    }
}
