using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RuBackend.Compatibility;
using RuBackend.Configuration;

namespace RuBackend.Memory;

public sealed class MemoryBusyException : Exception
{
    public MemoryBusyException()
        : base("Memory is busy (self-learning in progress); try again in a moment.")
    {
    }
}

public static class MemoryEndpoints
{
    private const string PythonOwnership = "python";
    private const string ShadowOwnership = "shadow";
    private const string AspNetOwnership = "aspnet";
    private const string BusyMessage = "Memory is busy (self-learning in progress); try again in a moment.";
    private const string ClearSuccessMessage = "Structured memory cleared. Session history was retained.";

    public static void Map(WebApplication app)
    {
        var ownership = app.Services
            .GetRequiredService<IOptions<BackendOptions>>()
            .Value
            .Ownership
            .Memory;

        switch (NormalizeOwnership(ownership))
        {
            case AspNetOwnership:
                app.MapGet("/memory", GetMemoryAsync);
                app.MapDelete("/memory", ClearMemoryAsync);
                break;
            case ShadowOwnership:
                app.MapGet("/memory", ShadowReadAsync);
                break;
            case PythonOwnership:
            default:
                // The catch-all route remains the single Python owner.
                break;
        }
    }

    private static async Task<IResult> GetMemoryAsync(
        IMemoryService memoryService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await memoryService.ListActiveAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(records.Select(ToDto).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure(loggerFactory, "ASP.NET memory read failed", exception);
            return Results.Json(
                new { status = "error", message = "Memory is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ClearMemoryAsync(
        IMemoryService memoryService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            await memoryService.ClearAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { status = "success", message = ClearSuccessMessage });
        }
        catch (MemoryBusyException)
        {
            return Results.Json(
                new { status = "error", message = BusyMessage },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFailure(loggerFactory, "ASP.NET memory clear failed", exception);
            return Results.Json(
                new { status = "error", message = "Memory could not be cleared; try again in a moment." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task ShadowReadAsync(
        HttpContext context,
        HttpForwarder forwarder,
        IMemoryService memoryService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var responseBody = await ForwardIntoBufferAsync(context, forwarder, cancellationToken)
            .ConfigureAwait(false);
        await context.Response.Body.WriteAsync(responseBody, cancellationToken).ConfigureAwait(false);

        if (context.Response.StatusCode != StatusCodes.Status200OK)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger("RuBackend.MemoryShadow");
        IReadOnlyList<MemoryItemDto>? pythonRecords;
        try
        {
            pythonRecords = JsonSerializer.Deserialize<List<MemoryItemDto>>(responseBody);
        }
        catch (JsonException)
        {
            logger.LogWarning(
                "Memory shadow comparison skipped: Python response is not a DTO array (status={StatusCode}, body_bytes={BodyBytes}).",
                context.Response.StatusCode,
                responseBody.Length);
            return;
        }

        if (pythonRecords is null)
        {
            logger.LogWarning(
                "Memory shadow comparison skipped: Python response is null (status={StatusCode}, body_bytes={BodyBytes}).",
                context.Response.StatusCode,
                responseBody.Length);
            return;
        }

        try
        {
            var aspNetRecords = await memoryService.ListActiveAsync(cancellationToken).ConfigureAwait(false);
            var pythonSnapshot = Snapshot(pythonRecords);
            var aspNetSnapshot = Snapshot(aspNetRecords.Select(ToDto));
            LogComparison(logger, pythonSnapshot, aspNetSnapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The Python response has already been written; do not turn a
            // client disconnect into a second response or a private log.
        }
        catch (Exception exception)
        {
            LogFailure(loggerFactory, "ASP.NET memory shadow read failed", exception);
        }
    }

    private static async Task<byte[]> ForwardIntoBufferAsync(
        HttpContext context,
        HttpForwarder forwarder,
        CancellationToken cancellationToken)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await forwarder.ForwardAsync(context, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static MemoryItemDto ToDto(MemoryRecord record) =>
        new(
            record.Id,
            record.Content,
            SerializeKind(record.Kind),
            FormatTimestamp(record.CreatedAt));

    private static string NormalizeOwnership(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            AspNetOwnership => AspNetOwnership,
            ShadowOwnership => ShadowOwnership,
            _ => PythonOwnership,
        };

    private static string SerializeKind(MemoryKind kind) => kind switch
    {
        MemoryKind.Profile => "profile",
        MemoryKind.Preference => "preference",
        MemoryKind.Project => "project",
        MemoryKind.Decision => "decision",
        MemoryKind.SessionSummary => "session_summary",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown memory kind."),
    };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz",
            CultureInfo.InvariantCulture);

    private static MemoryComparisonSnapshot Snapshot(IEnumerable<MemoryItemDto> records)
    {
        var items = records.ToArray();
        return new MemoryComparisonSnapshot(
            items.Length,
            HashValues(items.Select(item => item.Id)),
            HashValues(items.Select(item => SerializeKindValue(item.Category))),
            HashValues(items.Select(item => CanonicalizeTimestamp(item.CreatedAt))),
            HashValues(items.Select((item, index) => $"{index}:{item.Id}")));
    }

    private static string SerializeKindValue(string value) => value.Trim().ToLowerInvariant();

    private static string CanonicalizeTimestamp(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? FormatTimestamp(timestamp)
            : "invalid-timestamp";
    }

    private static string HashValues(IEnumerable<string> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void LogComparison(
        ILogger logger,
        MemoryComparisonSnapshot python,
        MemoryComparisonSnapshot aspNet)
    {
        var matches = python == aspNet;
        if (matches)
        {
            return;
        }

        logger.LogWarning(
            "Memory shadow mismatch: count_match={CountMatch}, id_hash_match={IdHashMatch}, category_hash_match={CategoryHashMatch}, timestamp_hash_match={TimestampHashMatch}, ordering_hash_match={OrderingHashMatch}, python_count={PythonCount}, aspnet_count={AspNetCount}.",
            python.Count == aspNet.Count,
            python.IdHash == aspNet.IdHash,
            python.CategoryHash == aspNet.CategoryHash,
            python.TimestampHash == aspNet.TimestampHash,
            python.OrderingHash == aspNet.OrderingHash,
            python.Count,
            aspNet.Count);
    }

    private static void LogFailure(ILoggerFactory loggerFactory, string message, Exception exception)
    {
        loggerFactory.CreateLogger("RuBackend.Memory").LogWarning(
            "{Message}; exception_type={ExceptionType}.",
            message,
            exception.GetType().Name);
    }

    private sealed record MemoryComparisonSnapshot(
        int Count,
        string IdHash,
        string CategoryHash,
        string TimestampHash,
        string OrderingHash);
}

public sealed record MemoryItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("created_at")] string CreatedAt);
