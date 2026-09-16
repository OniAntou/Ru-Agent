using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Diagnostics;

namespace RuBackend.Business.Activity;

public sealed class ActivityLogStore : IActivityLogStore
{
    private const int MaximumRead = 500;
    private const int MaximumEntries = 5_000;
    private const int MaximumRecordBytes = 256_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _logFile;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    public ActivityLogStore(DataPaths paths, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logFile = paths.ActivityLogFile;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task AppendAsync(
        ActivityEvent activity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var sanitized = ActivityPreview.SanitizeEvent(activity, _clock);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var eventId = sanitized.EventId;
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                if (_seenEventIds.Contains(eventId))
                {
                    return; // Duplicate event suppression
                }
            }

            var parent = Path.GetDirectoryName(_logFile)
                ?? throw new InvalidOperationException("Activity log path has no parent directory.");
            Directory.CreateDirectory(parent);

            var line = JsonSerializer.Serialize(sanitized, JsonOptions) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            await using (var stream = new FileStream(
                _logFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            // Mark the event only after the append succeeds. A failed write must
            // remain retryable instead of being suppressed as a duplicate.
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                _seenEventIds.Add(eventId);
                _seenOrder.Enqueue(eventId);
                if (_seenOrder.Count > MaximumEntries)
                {
                    var oldest = _seenOrder.Dequeue();
                    _seenEventIds.Remove(oldest);
                }
            }

            // Trim only when the file actually needs it. A small file is left
            // alone so steady appends never pay for a full scan-and-rewrite.
            var fileInfo = new FileInfo(_logFile);
            if (fileInfo.Length > 400_000 || _seenOrder.Count >= MaximumEntries)
            {
                await TrimUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ActivityEvent>> ReadAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaximumRead);
        byte[] snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_logFile))
            {
                return [];
            }

            // Snapshot the bytes under a short gate hold so appends and trims are
            // never stalled by parsing and re-sanitizing the whole file.
            try
            {
                snapshot = await File.ReadAllBytesAsync(_logFile, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }

        var events = new Queue<ActivityEvent>(MaximumEntries);
        var observedIds = new List<string>();
        using (var reader = new BoundedTextLineReader(new StreamReader(new MemoryStream(snapshot), Encoding.UTF8, detectEncodingFromByteOrderMarks: true), MaximumRecordBytes))
        {
            while (true)
            {
                var read = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (read.EndOfFile) break;
                if (read.TooLarge) continue;
                var line = read.Line!;
                var parsed = ParseEvent(line);
                if (parsed is not null)
                {
                    // Re-sanitize on read as a defense-in-depth boundary for
                    // legacy files written before the privacy policy existed.
                    if (events.Count == MaximumEntries)
                    {
                        events.Dequeue();
                    }

                    var sanitized = ActivityPreview.SanitizeEvent(parsed, _clock);
                    events.Enqueue(sanitized);
                    if (!string.IsNullOrWhiteSpace(sanitized.EventId))
                    {
                        observedIds.Add(sanitized.EventId);
                    }
                }
            }
        }

        // Warm the in-memory dedup set so a redelivery after a process restart
        // does not re-append rows that are already on disk.
        if (observedIds.Count > 0)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var eventId in observedIds)
                {
                    if (_seenEventIds.Add(eventId))
                    {
                        _seenOrder.Enqueue(eventId);
                        if (_seenOrder.Count > MaximumEntries)
                        {
                            var oldest = _seenOrder.Dequeue();
                            _seenEventIds.Remove(oldest);
                        }
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return events
            .OrderBy(activity => activity.Ts)
            .TakeLast(boundedLimit)
            .ToArray();
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(_logFile);
            _seenEventIds.Clear();
            _seenOrder.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TrimUnlockedAsync(CancellationToken cancellationToken)
    {
        // Fast path: a small file never needs a rewrite, so skip the scan.
        var fileInfo = new FileInfo(_logFile);
        if (fileInfo.Length <= 400_000)
        {
            return;
        }

        var retained = new Queue<string>(MaximumEntries);
        var total = 0;
        await using (var stream = new FileStream(
            _logFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            useAsync: true))
        using (var reader = new BoundedTextLineReader(new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true), MaximumRecordBytes))
        {
            while (true)
            {
                var read = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (read.EndOfFile) break;
                if (read.TooLarge) continue;
                var line = read.Line!;
                total++;
                if (retained.Count == MaximumEntries)
                {
                    retained.Dequeue();
                }

                retained.Enqueue(line);
            }
        }

        if (total <= MaximumEntries)
        {
            return;
        }

        await WriteAtomicAsync(
            string.Join(Environment.NewLine, retained) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAtomicAsync(
        string content,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(_logFile)
            ?? throw new InvalidOperationException("Activity log path has no parent directory.");
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(_logFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _logFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static ActivityEvent? ParseEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(line) is not JsonObject root ||
                !TryReadNumber(root["ts"], out var timestamp) ||
                !TryReadString(root["iso"], out var iso) ||
                !TryReadString(root["type"], out var type))
            {
                return null;
            }

            string? eventId = null;
            if (root["event_id"] is JsonValue eventIdValue &&
                eventIdValue.TryGetValue<string>(out var parsedEventId))
            {
                eventId = parsedEventId;
            }

            string? sessionId = null;
            if (root["session_id"] is JsonValue sessionValue &&
                sessionValue.TryGetValue<string>(out var parsedSession))
            {
                sessionId = parsedSession;
            }

            var data = root["data"] is JsonObject dataObject
                ? dataObject
                : [];

            return new ActivityEvent(eventId, timestamp, iso, type, sessionId, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var parsed))
        {
            value = parsed;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadNumber(JsonNode? node, out double value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var integer))
            {
                value = integer;
                return true;
            }

            if (jsonValue.TryGetValue<long>(out var longInteger))
            {
                value = longInteger;
                return true;
            }

            if (jsonValue.TryGetValue<double>(out value) && double.IsFinite(value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
