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
        var (thread, release) = HoldLockOnWorkerThread(() => PublicHostSingleInstance.AcquireForDataRoot(_root));
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

    [Fact]
    public async Task SameLogRootOnAnotherDataRootFailsClosedBeforeTheBrokerOrDescriptor()
    {
        var logRoot = Path.Combine(_root, "logs");
        var firstDataRoot = Path.Combine(_root, "data-a");
        var secondDataRoot = Path.Combine(_root, "data-b");
        var firstPort = FindFreePort();
        var (thread, release) = HoldLockOnWorkerThread(
            () => PublicHostSingleInstance.AcquireForPortAndDataRoot(firstPort, firstDataRoot, logRoot));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RU_DATA_ROOT"] = secondDataRoot,
                    ["RU_LOG_ROOT"] = logRoot,
                    ["urls"] = $"http://127.0.0.1:{FindFreePort()}",
                })
                .Build();

            var error = await Assert.ThrowsAsync<HostStartupException>(
                () => ManagedRuntimeBootstrapper.StartAsync(configuration, _root));

            Assert.Equal(3, error.ExitCode);
            Assert.Contains("public_host_already_running", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(secondDataRoot, "runtime_descriptor.json")));
            Assert.False(Directory.Exists(Path.Combine(logRoot, "agent", "session")));
        }
        finally
        {
            release.Set();
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static (Thread Thread, ManualResetEventSlim Release) HoldLockOnWorkerThread(
        Func<PublicHostSingleInstance> acquire)
    {
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        PublicHostSingleInstance? guard = null;
        var thread = new Thread(() =>
        {
            guard = acquire();
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            guard.Dispose();
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "holder thread did not acquire the lock");
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
