namespace RuBackend.Diagnostics;

using System.Text;
using System.Text.Json.Serialization;

public sealed record DiagnosticLogQuery(
    [property: JsonPropertyName("limit")] int Limit = 100,
    [property: JsonPropertyName("since")] string? Since = null,
    [property: JsonPropertyName("level")] string? Level = null,
    [property: JsonPropertyName("component")] string? Component = null,
    [property: JsonPropertyName("event_code")] string? EventCode = null,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId = null,
    [property: JsonPropertyName("cursor")] string? Cursor = null);

public sealed record DiagnosticLogResult(
    [property: JsonPropertyName("events")] IReadOnlyList<DiagnosticEvent> Events,
    [property: JsonPropertyName("cursor")] string? Cursor);

public sealed class DiagnosticLogReader
{
    private readonly string _logDirectory;

    public DiagnosticLogReader(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public async Task<DiagnosticLogResult> ReadLogsAsync(DiagnosticLogQuery query, CancellationToken cancellationToken = default)
    {
        DiagnosticLogQueryPolicy.Validate(query);
        var limit = query.Limit;

        if (!Directory.Exists(_logDirectory))
        {
            return new DiagnosticLogResult(Array.Empty<DiagnosticEvent>(), null);
        }

        double? sinceTs = null;
        DateOnly? sinceDate = null;
        if (!string.IsNullOrWhiteSpace(query.Since))
        {
            DiagnosticLogQueryPolicy.TryParseSince(query.Since, out var parsedSinceTs, out var parsedSinceDate);
            sinceTs = parsedSinceTs;
            sinceDate = parsedSinceDate;
        }

        var candidateFiles = Directory.GetFiles(_logDirectory, "*.jsonl")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                var validPrefix = name.StartsWith("aspnetcore-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("worker-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("gui-", StringComparison.OrdinalIgnoreCase);
                if (!validPrefix)
                {
                    return false;
                }

                if (sinceDate.HasValue)
                {
                    var dashIdx = name.IndexOf('-');
                    if (dashIdx >= 0 && name.Length >= dashIdx + 11)
                    {
                        var dateStr = name.Substring(dashIdx + 1, 10);
                        if (DateOnly.TryParse(dateStr, out var fileDate) && fileDate < sinceDate.Value)
                        {
                            return false;
                        }
                    }
                }

                return true;
            })
            .OrderBy(f => f)
            .ToList();

        var collectedEvents = new List<DiagnosticEvent>();
        var maxBufferSize = Math.Min(limit * 3, 10000);

        // Parse cursor if present: format "cur_{ts}_{eventId}"
        double? cursorTs = null;
        string? cursorEventId = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            DiagnosticLogQueryPolicy.TryParseCursor(query.Cursor, out var parsedCursorTs, out cursorEventId);
            cursorTs = parsedCursorTs;
        }

        foreach (var file in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var scannedBytes = 0;
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                    if (lineBytes > DiagnosticLogQueryPolicy.MaxRecordBytes)
                    {
                        continue;
                    }

                    if (scannedBytes + lineBytes > DiagnosticLogQueryPolicy.MaxScanBytesPerFile)
                    {
                        break;
                    }

                    scannedBytes += lineBytes;

                    var evt = DiagnosticEventPolicy.Deserialize(line);
                    if (evt == null)
                    {
                        continue;
                    }

                    if (sinceTs.HasValue && evt.Ts < sinceTs.Value)
                    {
                        continue;
                    }

                    if (cursorTs.HasValue)
                    {
                        if (evt.Ts < cursorTs.Value)
                        {
                            continue;
                        }
                        if (Math.Abs(evt.Ts - cursorTs.Value) < 0.000001 && !string.IsNullOrEmpty(cursorEventId))
                        {
                            if (string.Compare(evt.EventId, cursorEventId, StringComparison.Ordinal) <= 0)
                            {
                                continue;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(query.Level) && !string.Equals(evt.Level, query.Level.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(query.Component) && !evt.Component.StartsWith(query.Component.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(query.EventCode) && !string.Equals(evt.EventCode, query.EventCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(query.CorrelationId) && !string.Equals(evt.CorrelationId, query.CorrelationId.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    collectedEvents.Add(evt);
                    if (collectedEvents.Count >= maxBufferSize)
                    {
                        // Keep only the newest items within buffer size
                        collectedEvents = collectedEvents
                            .OrderBy(e => e.Ts)
                            .ThenBy(e => e.EventId, StringComparer.Ordinal)
                            .TakeLast(limit)
                            .ToList();
                    }
                }
            }
            catch (IOException)
            {
                // Skip temporarily locked files safely
            }
            catch (UnauthorizedAccessException)
            {
                // Skip files that become inaccessible during a read.
            }
        }

        var sorted = collectedEvents
            .OrderBy(e => e.Ts)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        string? nextCursor = null;
        if (sorted.Count > 0)
        {
            var last = sorted[^1];
            nextCursor = $"cur_{last.Ts:F6}_{last.EventId}";
        }

        return new DiagnosticLogResult(sorted, nextCursor);
    }
}
