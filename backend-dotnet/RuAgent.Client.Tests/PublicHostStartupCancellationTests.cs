using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class PublicHostStartupCancellationTests
{
    [Fact]
    public async Task Caller_cancellation_at_process_start_is_preserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-start-cancel-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "RuBackend.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, []);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var starter = new PublicHostStarter(new PublicHostLocator(root),
                startProcess: _ =>
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.GetOrStartAsync(root, cancellation.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
