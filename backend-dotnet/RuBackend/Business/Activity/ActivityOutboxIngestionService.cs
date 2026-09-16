namespace RuBackend.Business.Activity;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using RuBackend.Diagnostics;

public sealed class ActivityOutboxIngestionService : BackgroundService
{
    private const int MaximumRecordBytes = 256_000;
    private const int MaximumLeftoverFilesPerPass = 16;
    private readonly IActivityLogStore _activityLogStore;
    private readonly string _logRoot;
    private readonly string _outboxFile;
    private readonly TimeSpan _pollingInterval;

    public ActivityOutboxIngestionService(
        IActivityLogStore activityLogStore,
        DataPaths paths,
        TimeSpan? pollingInterval = null)
    {
        _activityLogStore = activityLogStore ?? throw new ArgumentNullException(nameof(activityLogStore));
        ArgumentNullException.ThrowIfNull(paths);
        _logRoot = paths.LogRoot;
        _outboxFile = Path.Combine(paths.LogRoot, "activity_outbox.jsonl");
        _pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<int> IngestPendingAsync(CancellationToken cancellationToken = default)
    {
        var totalIngested = 0;

        // 1. Process any leftover .processing files from previous runs.
        // Capped per pass so a crash burst cannot stall this poll iteration.
        if (Directory.Exists(_logRoot))
        {
            var leftoverFiles = Directory.GetFiles(_logRoot, "activity_outbox.*.processing");
            Array.Sort(leftoverFiles, StringComparer.Ordinal);
            foreach (var leftover in leftoverFiles.Take(MaximumLeftoverFilesPerPass))
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalIngested += await IngestProcessingFileAsync(leftover, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2. Atomically rename current outbox file if present and non-empty
        if (!File.Exists(_outboxFile))
        {
            return totalIngested;
        }

        var processingFile = Path.Combine(_logRoot, $"activity_outbox.{Guid.NewGuid():N}.processing");
        try
        {
            File.Move(_outboxFile, processingFile);
        }
        catch (IOException)
        {
            // File might be in use or being written by worker
            return totalIngested;
        }
        catch (UnauthorizedAccessException)
        {
            return totalIngested;
        }

        totalIngested += await IngestProcessingFileAsync(processingFile, cancellationToken).ConfigureAwait(false);
        return totalIngested;
    }

    private async Task<int> IngestProcessingFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return 0;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        await using var ownedStream = stream;
        using var reader = new BoundedTextLineReader(new StreamReader(ownedStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true), MaximumRecordBytes);
        var ingestedCount = 0;
        while (true)
        {
            var read = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (read.EndOfFile) break;
            if (read.TooLarge) continue;
            var line = read.Line!;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject root)
                {
                    var evt = new ActivityEvent(
                        ReadOptionalString(root, "event_id"),
                        ReadNumber(root, "ts"),
                        ReadOptionalString(root, "iso") ?? string.Empty,
                        ReadOptionalString(root, "type") ?? "chat",
                        ReadOptionalString(root, "session_id"),
                        root.TryGetPropertyValue("data", out var datav) && datav is JsonObject d ? d : new JsonObject());
                    try
                    {
                        await _activityLogStore.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
                        ingestedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // One poison row must not abort the rest of the file.
                        // Duplicates from a later re-ingest are suppressed by
                        // the store's event-id dedup.
                    }
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                // Skip malformed line
            }
        }

        try
        {
            File.Delete(filePath);
        }
        catch { }

        return ingestedCount;
    }

    private static string? ReadOptionalString(JsonObject root, string key)
    {
        if (root.TryGetPropertyValue(key, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    private static double ReadNumber(JsonObject root, string key)
    {
        if (!root.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<long>(out var longInteger))
        {
            return longInteger;
        }

        if (value.TryGetValue<double>(out var number) && double.IsFinite(number))
        {
            return number;
        }

        return 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IngestPendingAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_pollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Prevent crash on loop iteration, but back off so a persistent
                // failure cannot spin the loop.
                try
                {
                    await Task.Delay(_pollingInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
