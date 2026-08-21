namespace RuBackend.Diagnostics;

using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RuBackend.Business;

public static class DiagnosticEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/diagnostics/logs", GetLogsAsync);
        app.MapGet("/diagnostics/export", ExportLogsAsync);
        app.MapGet("/diagnostics/status", GetStatusAsync);
    }

    public static async Task<IResult> GetLogsAsync(
        [FromQuery] int? limit,
        [FromQuery] string? since,
        [FromQuery] string? level,
        [FromQuery] string? component,
        [FromQuery] string? event_code,
        [FromQuery] string? correlation_id,
        [FromQuery] string? cursor,
        [FromServices] DataPaths paths,
        CancellationToken cancellationToken)
    {
        var reader = new DiagnosticLogReader(paths.LogRoot);
        var query = new DiagnosticLogQuery(
            Limit: limit ?? 100,
            Since: since,
            Level: level,
            Component: component,
            EventCode: event_code,
            CorrelationId: correlation_id,
            Cursor: cursor);

        try
        {
            var result = await reader.ReadLogsAsync(query, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (DiagnosticQueryValidationException error)
        {
            return Results.BadRequest(new { error_code = error.ErrorCode });
        }
    }

    public static async Task<IResult> ExportLogsAsync(
        [FromQuery] string? since,
        [FromQuery] string? level,
        [FromQuery] string? component,
        [FromQuery] string? event_code,
        [FromQuery] string? correlation_id,
        [FromServices] DataPaths paths,
        CancellationToken cancellationToken)
    {
        var reader = new DiagnosticLogReader(paths.LogRoot);
        var query = new DiagnosticLogQuery(
            Limit: 5000,
            Since: since,
            Level: level,
            Component: component,
            EventCode: event_code,
            CorrelationId: correlation_id,
            Cursor: null);

        DiagnosticLogResult result;
        try
        {
            result = await reader.ReadLogsAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (DiagnosticQueryValidationException error)
        {
            return Results.BadRequest(new { error_code = error.ErrorCode });
        }

        await using var output = new MemoryStream();
        foreach (var evt in result.Events)
        {
            var line = Encoding.UTF8.GetBytes(DiagnosticEventPolicy.Serialize(evt) + Environment.NewLine);
            if (output.Length + line.Length > DiagnosticLogQueryPolicy.MaxExportBytes)
            {
                return Results.Json(
                    new { error_code = "diagnostic_export_too_large" },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            await output.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }

        return Results.File(
            output.ToArray(),
            "application/x-ndjson",
            "diagnostics-export.ndjson");
    }

    public static IResult GetStatusAsync(
        [FromServices] DataPaths paths,
        [FromServices] StructuredDiagnosticLogWriter? writer = null)
    {
        var logDir = paths.LogRoot;
        long totalBytes = 0;
        var fileCount = 0;

        if (Directory.Exists(logDir))
        {
            var files = Directory.GetFiles(logDir, "*.jsonl");
            fileCount = files.Length;
            foreach (var f in files)
            {
                try
                {
                    totalBytes += new FileInfo(f).Length;
                }
                catch { }
            }
        }

        var snapshot = writer?.Queue.GetSnapshot(totalBytes, fileCount) ?? new DiagnosticStatusSnapshot(
            QueueDepth: 0,
            EnqueuedCount: 0,
            DroppedCount: 0,
            WriteFailureCount: 0,
            TotalBytes: totalBytes,
            ActiveFilesCount: fileCount);

        var response = new
        {
            status = "healthy",
            total_log_files = fileCount,
            total_size_bytes = totalBytes,
            writer = snapshot
        };

        return Results.Ok(response);
    }
}
