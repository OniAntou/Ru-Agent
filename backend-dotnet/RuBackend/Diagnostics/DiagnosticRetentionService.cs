namespace RuBackend.Diagnostics;

using Microsoft.Extensions.Hosting;

public sealed class DiagnosticRetentionService : BackgroundService
{
    private readonly string _logDirectory;
    private readonly int _maxAgeDays;
    private readonly long _maxSizeBytes;
    private readonly TimeSpan _checkInterval;

    public DiagnosticRetentionService(
        string logDirectory,
        int maxAgeDays = 7,
        long maxSizeBytes = 100 * 1024 * 1024,
        TimeSpan? checkInterval = null)
    {
        _logDirectory = logDirectory;
        _maxAgeDays = Math.Max(1, maxAgeDays);
        _maxSizeBytes = Math.Max(1024 * 1024, maxSizeBytes);
        _checkInterval = checkInterval ?? TimeSpan.FromHours(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunRetentionCleanupAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(_checkInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public Task RunRetentionCleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_logDirectory))
        {
            return Task.CompletedTask;
        }

        try
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-_maxAgeDays);
            var files = new DirectoryInfo(_logDirectory)
                .GetFiles("*.jsonl")
                .Where(f => f.Name.StartsWith("aspnetcore-", StringComparison.OrdinalIgnoreCase)
                    || f.Name.StartsWith("worker-", StringComparison.OrdinalIgnoreCase)
                    || f.Name.StartsWith("gui-", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, "activity_outbox.jsonl", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 1. Age-based cleanup
            var remainingFiles = new List<FileInfo>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.LastWriteTimeUtc < cutoffUtc)
                {
                    TryDeleteFile(file);
                }
                else
                {
                    remainingFiles.Add(file);
                }
            }

            // 2. Size-based cleanup (FIFO: oldest files deleted first)
            long totalBytes = 0;
            foreach (var file in remainingFiles)
            {
                try
                {
                    totalBytes += file.Length;
                }
                catch { }
            }

            if (totalBytes > _maxSizeBytes)
            {
                var sortedByAge = remainingFiles
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                foreach (var file in sortedByAge)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalBytes <= _maxSizeBytes)
                    {
                        break;
                    }

                    try
                    {
                        var len = file.Length;
                        if (TryDeleteFile(file))
                        {
                            totalBytes -= len;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tolerate locked or unavailable directory safely
        }

        return Task.CompletedTask;
    }

    public (long TotalBytes, int ActiveFilesCount) GetMetrics()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return (0, 0);
        }

        try
        {
            var files = new DirectoryInfo(_logDirectory)
                .GetFiles("*.jsonl")
                .Where(f => f.Name.StartsWith("aspnetcore-", StringComparison.OrdinalIgnoreCase)
                    || f.Name.StartsWith("worker-", StringComparison.OrdinalIgnoreCase)
                    || f.Name.StartsWith("gui-", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, "activity_outbox.jsonl", StringComparison.OrdinalIgnoreCase))
                .ToList();

            long totalBytes = 0;
            foreach (var file in files)
            {
                try { totalBytes += file.Length; } catch { }
            }

            return (totalBytes, files.Count);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool TryDeleteFile(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
