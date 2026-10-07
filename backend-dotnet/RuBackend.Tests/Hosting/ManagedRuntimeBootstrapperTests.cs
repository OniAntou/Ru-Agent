using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using RuAgent.Hosting;
using RuBackend.Hosting;
using Xunit;

namespace RuBackend.Tests.Hosting;

public sealed class ManagedRuntimeBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-bootstrap-" + Guid.NewGuid().ToString("N"));

    public ManagedRuntimeBootstrapperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task SameDataRootOnAnotherPortFailsClosedBeforeTheBrokerOrDescriptor()
    {
        var (thread, release) = HoldDataRootOnWorkerThread(_root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RU_DATA_ROOT"] = _root,
                    ["RU_LOG_ROOT"] = Path.Combine(_root, "logs"),
                    ["urls"] = $"http://127.0.0.1:{FindFreePort()}",
                })
                .Build();

            var error = await Assert.ThrowsAsync<HostStartupException>(
                () => ManagedRuntimeBootstrapper.StartAsync(configuration, _root));

            Assert.Equal(3, error.ExitCode);
            Assert.Contains("public_host_already_running", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_root, "runtime_descriptor.json")));
            Assert.False(Directory.Exists(Path.Combine(_root, "logs", "agent", "session")));
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static (Thread Thread, ManualResetEventSlim Release) HoldDataRootOnWorkerThread(string dataRoot)
    {
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        PublicHostSingleInstance? guard = null;
        var thread = new Thread(() =>
        {
            guard = PublicHostSingleInstance.AcquireForDataRoot(dataRoot);
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            guard.Dispose();
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "holder thread did not acquire the data root");
        Assert.True(guard!.IsAcquired);
        return (thread, release);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
