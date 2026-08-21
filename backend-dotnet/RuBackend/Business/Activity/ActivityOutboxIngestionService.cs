namespace RuBackend.Business.Activity;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

public sealed class ActivityOutboxIngestionService : BackgroundService
{
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

        // 1. Process any leftover .processing files from previous runs
        if (Directory.Exists(_logRoot))
        {
            var leftoverFiles = Directory.GetFiles(_logRoot, "activity_outbox.*.processing");
            foreach (var leftover in leftoverFiles)
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

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        var ingestedCount = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject root)
                {
                    var ts = root.TryGetPropertyValue("ts", out var tsv) && tsv is JsonValue tv && tv.TryGetValue<double>(out var t) ? t : 0;
                    var iso = root.TryGetPropertyValue("iso", out var isov) && isov is JsonValue isv ? isv.GetValue<string>() : string.Empty;
                    var type = root.TryGetPropertyValue("type", out var typev) && typev is JsonValue tyv ? tyv.GetValue<string>() : "chat";
                    var eventId = root.TryGetPropertyValue("event_id", out var eidv) && eidv is JsonValue eiv ? eiv.GetValue<string>() : null;
                    var sessionId = root.TryGetPropertyValue("session_id", out var sidv) && sidv is JsonValue sv ? sv.GetValue<string>() : null;
                    var data = root.TryGetPropertyValue("data", out var datav) && datav is JsonObject d ? d : new JsonObject();

                    var evt = new ActivityEvent(eventId, ts, iso, type, sessionId, data);
                    await _activityLogStore.AppendAsync(evt, cancellationToken).ConfigureAwait(false);
                    ingestedCount++;
                }
            }
            catch (JsonException)
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
                // Prevent crash on loop iteration
            }
        }
    }
}
