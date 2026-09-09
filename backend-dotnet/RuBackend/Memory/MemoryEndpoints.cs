using System.Globalization;
using System.Text.Json.Serialization;

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
    private const string BusyMessage = "Memory is busy (self-learning in progress); try again in a moment.";
    private const string ClearSuccessMessage = "Structured memory cleared. Session history was retained.";
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public static void Map(WebApplication app)
    {
        app.MapGet("/memory", GetMemoryAsync);
        app.MapDelete("/memory", ClearMemoryAsync);
    }

    private static async Task<IResult> GetMemoryAsync(
        HttpContext context,
        IMemoryService memoryService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var limitRaw = context.Request.Query["limit"].ToString();
            var cursor = context.Request.Query["cursor"].ToString();
            var limit = DefaultLimit;

            if (!string.IsNullOrWhiteSpace(limitRaw) && int.TryParse(limitRaw, out var parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, MaxLimit);
            }

            if (string.IsNullOrWhiteSpace(cursor))
            {
                cursor = null;
            }

            var records = await memoryService.ListActiveAsync(limit, cursor, cancellationToken).ConfigureAwait(false);
            var items = records.Select(ToDto).ToArray();

            string? nextCursor = null;
            if (items.Length == limit)
            {
                var last = items[^1];
                var record = await memoryService.GetAsync(last.Id, cancellationToken).ConfigureAwait(false);
                if (record is not null)
                {
                    nextCursor = $"{record.UpdatedAt.UtcTicks}|{record.Id}";
                }
            }

            var response = new
            {
                items,
                next_cursor = nextCursor
            };

            return Results.Ok(response);
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

    private static MemoryItemDto ToDto(MemoryRecord record) =>
        new(
            record.Id,
            record.Content,
            SerializeKind(record.Kind),
            FormatTimestamp(record.CreatedAt));

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

    private static void LogFailure(ILoggerFactory loggerFactory, string message, Exception exception)
    {
        loggerFactory.CreateLogger("RuBackend.Memory").LogWarning(
            "{Message}; exception_type={ExceptionType}.",
            message,
            exception.GetType().Name);
    }

}

public sealed record MemoryItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("created_at")] string CreatedAt);
