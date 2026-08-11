using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Business.Activity;

public sealed class ActivityLogStore : IActivityLogStore
{
    private const int MaximumRead = 500;
    private const int MaximumEntries = 5_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly string[] SensitiveKeyFragments =
    [
        "api_key",
        "password",
        "secret",
        "token",
        "audio",
        "voice",
        "vts",
        "codes",
        "facial",
        "body",
        "emotion",
        "marker",
    ];

    private readonly string _logFile;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow().ToUniversalTime();
            var normalized = new ActivityEvent(
                !double.IsFinite(activity.Ts) || activity.Ts <= 0
                    ? now.ToUnixTimeMilliseconds() / 1000d
                    : activity.Ts,
                string.IsNullOrWhiteSpace(activity.Iso) ? now.ToString("O") : activity.Iso,
                string.IsNullOrWhiteSpace(activity.Type) ? "unknown" : activity.Type,
                activity.SessionId,
                SanitizeObject(activity.Data));

            var parent = Path.GetDirectoryName(_logFile)
                ?? throw new InvalidOperationException("Activity log path has no parent directory.");
            Directory.CreateDirectory(parent);
            var line = JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            await using (var stream = new FileStream(
                _logFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await TrimUnlockedAsync(cancellationToken).ConfigureAwait(false);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_logFile))
            {
                return [];
            }

            var events = new List<ActivityEvent>();
            foreach (var line in await File.ReadAllLinesAsync(_logFile, cancellationToken)
                         .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = ParseEvent(line);
                if (parsed is not null)
                {
                    events.Add(parsed);
                }
            }

            return events
                .OrderBy(activity => activity.Ts)
                .TakeLast(boundedLimit)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(_logFile);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TrimUnlockedAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(_logFile, cancellationToken)
            .ConfigureAwait(false);
        if (lines.Length <= MaximumEntries)
        {
            return;
        }

        var retained = lines.TakeLast(MaximumEntries);
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
                FileOptions.Asynchronous | FileOptions.WriteThrough))
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

            string? sessionId = null;
            if (root["session_id"] is JsonValue sessionValue &&
                sessionValue.TryGetValue<string>(out var parsedSession))
            {
                sessionId = parsedSession;
            }

            var data = root["data"] is JsonObject dataObject
                ? SanitizeObject(dataObject)
                : [];
            return new ActivityEvent(timestamp, iso, type, sessionId, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject SanitizeObject(JsonObject source)
    {
        var sanitized = new JsonObject();
        foreach (var property in source)
        {
            if (IsSensitiveKey(property.Key))
            {
                continue;
            }

            sanitized[property.Key] = SanitizeNode(property.Value);
        }

        return sanitized;
    }

    private static JsonNode? SanitizeNode(JsonNode? node)
    {
        return node switch
        {
            JsonObject nestedObject => SanitizeObject(nestedObject),
            JsonArray nestedArray => new JsonArray(
                nestedArray.Select(SanitizeNode).ToArray()),
            null => null,
            _ => node.DeepClone(),
        };
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Trim().ToLowerInvariant().Replace('-', '_');
        return SensitiveKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
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
