using Microsoft.Extensions.DependencyInjection;
using RuBackend.Business;
using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class DiagnosticWriterHostShutdownTests
{
    [Fact]
    public void Host_shutdown_observes_writer_outcome_before_container_disposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-host-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new DataPaths(
            Path.Combine(root, "memory.db"),
            Path.Combine(root, "memory.json"),
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        var factory = new BackendWebApplicationFactory(dataPaths: paths);

        try
        {
            using var client = factory.CreateClient();
            var writer = factory.Services.GetRequiredService<StructuredDiagnosticLogWriter>();
            var lifetime = factory.Services.GetRequiredService<StructuredDiagnosticLogWriterHostedService>();
            Assert.True(writer.TryEnqueue(new DiagnosticEvent(
                "host-shutdown", 1720000000, "2024-07-03T09:46:40Z", "info", "host", "host_startup")));

            factory.Dispose();

            Assert.Equal(DiagnosticShutdownOutcome.Drained, lifetime.LastShutdownOutcome);
            Assert.True(writer.Completion.IsCompleted);
        }
        finally
        {
            factory.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
