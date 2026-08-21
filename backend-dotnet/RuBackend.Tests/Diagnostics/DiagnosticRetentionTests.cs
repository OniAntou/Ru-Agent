using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticRetentionTests : IDisposable
{
    private readonly string _tempLogDir;

    public DiagnosticRetentionTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "ru-diag-retention-test-" + Guid.NewGuid().ToString("N"));
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
    public async Task Retention_cleans_files_older_than_seven_days()
    {
        var oldFile = Path.Combine(_tempLogDir, "aspnetcore-2026-08-01.jsonl");
        var recentFile = Path.Combine(_tempLogDir, "aspnetcore-2026-08-20.jsonl");

        await File.WriteAllTextAsync(oldFile, "{}");
        await File.WriteAllTextAsync(recentFile, "{}");

        // Set creation/write times
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow);

        var retention = new DiagnosticRetentionService(_tempLogDir, maxAgeDays: 7, maxSizeBytes: 100 * 1024 * 1024);
        await retention.RunRetentionCleanupAsync();

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public async Task Retention_tolerates_missing_or_locked_directories_safely()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "ru-missing-" + Guid.NewGuid().ToString("N"));
        var retention = new DiagnosticRetentionService(nonexistent);

        // Should not throw
        await retention.RunRetentionCleanupAsync();
    }
}
