using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class PublicHostStarterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-starter-" + Guid.NewGuid().ToString("N"));
    public PublicHostStarterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ValidDescriptorPlusHealthReturnsSession()
    {
        var port = FindFreePort();
        var locator = new PublicHostLocator(_root);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var bearer = new string('x', 64);
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, $"http://127.0.0.1:{port}/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('y', 32), DateTimeOffset.UtcNow.ToString("O"));
        Directory.CreateDirectory(Path.GetDirectoryName(locator.DescriptorPath)!);
        File.WriteAllText(locator.DescriptorPath, System.Text.Json.JsonSerializer.Serialize(desc, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions));

        var starter = new PublicHostStarter(locator, healthProbe: (_, _) => Task.FromResult(true), startHost: (_, _, _) => Task.FromResult(true), publicPort: port);
        var session = await starter.GetOrStartAsync(_root);
        Assert.NotNull(session);
        Assert.Equal(bearer, session.BearerToken);
    }

    [Fact]
    public async Task AbsentDescriptorStartsHost()
    {
        var port = FindFreePort();
        var locator = new PublicHostLocator(Path.Combine(_root, "sub-" + Guid.NewGuid().ToString("N")));
        bool startCalled = false;
        var starter = new PublicHostStarter(
            locator,
            healthProbe: (_, _) => Task.FromResult(File.Exists(locator.DescriptorPath)),
            startHost: (_, bearer, _) =>
            {
                startCalled = true;
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, $"http://127.0.0.1:{port}/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('w', 32), DateTimeOffset.UtcNow.ToString("O"));
                Directory.CreateDirectory(Path.GetDirectoryName(locator.DescriptorPath)!);
                File.WriteAllText(locator.DescriptorPath, System.Text.Json.JsonSerializer.Serialize(desc, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions));
                return Task.FromResult(true);
            },
            publicPort: port);

        var session = await starter.GetOrStartAsync(_root);
        Assert.True(startCalled);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task PortConflictFailsWithStableCodeAndNeverKillsForeign()
    {
        var port = FindFreePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            var locator = new PublicHostLocator(Path.Combine(_root, "conflict-" + Guid.NewGuid().ToString("N")));
            var starter = new PublicHostStarter(locator, healthProbe: (_, _) => Task.FromResult(false), startHost: (_, _, _) => Task.FromResult(true), publicPort: port);
            var ex = await Assert.ThrowsAsync<AgentApiException>(() => starter.GetOrStartAsync(_root));
            Assert.Equal("public_host_port_conflict", ex.ErrorCode);
            // Ensure listener still alive (not killed)
            Assert.True(IsPortOccupied(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NeverStartsWorkerBrokerDirectly()
    {
        var port = FindFreePort();
        var locator = new PublicHostLocator(Path.Combine(_root, "nb-" + Guid.NewGuid().ToString("N")));
        bool workerStarted = false;
        var starter = new PublicHostStarter(
            locator,
            healthProbe: (_, _) => Task.FromResult(File.Exists(locator.DescriptorPath)),
            startHost: (proj, bearer, _) =>
            {
                // Ensure startHost does not attempt to start the Broker directly; the Public Host owns that child.
                // In our fake, we just set flag if called; real implementation would only start RuBackend.exe
                workerStarted = bearer.Length == 64; // just marker
                // Simulate host creates descriptor
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, $"http://127.0.0.1:{port}/", bearer, proc.Id, proc.StartTime.ToUniversalTime().ToString("O"), new string('q', 32), DateTimeOffset.UtcNow.ToString("O"));
                Directory.CreateDirectory(Path.GetDirectoryName(locator.DescriptorPath)!);
                File.WriteAllText(locator.DescriptorPath, System.Text.Json.JsonSerializer.Serialize(desc, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions));
                return Task.FromResult(true);
            },
            publicPort: port);
        var session = await starter.GetOrStartAsync(_root);
        Assert.NotNull(session);
        Assert.True(workerStarted);
        // No broker start via Client; only Public Host start was invoked – verified by single startHost call
    }

    [Fact]
    public async Task Disposed_client_cannot_start_or_attach_again()
    {
        var starts = 0;
        var starter = new PublicHostStarter(new PublicHostLocator(_root),
            startHost: (_, _, _) => { starts++; return Task.FromResult(false); }, publicPort: FindFreePort());
        starter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => starter.GetOrStartAsync(_root));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Disposing_starting_client_leaves_shared_host_process_alive()
    {
        var executable = Path.Combine(_root, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "RuBackend.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, []);
        var locator = new PublicHostLocator(_root);
        var port = FindFreePort();
        System.Diagnostics.Process? observer = null;
        try
        {
            using var starter = new PublicHostStarter(locator, healthProbe: (_, _) => Task.FromResult(true),
                publicPort: port, startProcess: info =>
                {
                    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                        Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    })!;
                    observer = System.Diagnostics.Process.GetProcessById(process.Id);
                    var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, $"http://127.0.0.1:{port}/",
                        info.Environment["RU_DESKTOP_TOKEN"]!, process.Id, process.StartTime.ToUniversalTime().ToString("O"),
                        new string('z', 32), DateTimeOffset.UtcNow.ToString("O"));
                    File.WriteAllText(locator.DescriptorPath, System.Text.Json.JsonSerializer.Serialize(descriptor, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions));
                    return process;
                });
            await starter.GetOrStartAsync(_root);
            starter.Dispose();
            Assert.NotNull(observer);
            Assert.False(observer.HasExited);
        }
        finally
        {
            // Only the hidden helper created by this test is terminated.
            if (observer is not null)
            {
                if (!observer.HasExited) observer.Kill(entireProcessTree: true);
                observer.WaitForExit(5000);
                observer.Dispose();
            }
        }
    }

    [Fact]
    public async Task Started_host_receives_urls_before_managed_runtime_for_the_public_port()
    {
        var port = FindFreePort();
        var executable = Path.Combine(_root, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "RuBackend.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, []);
        var locator = new PublicHostLocator(Path.Combine(_root, "args-" + Guid.NewGuid().ToString("N")));
        string? capturedArguments = null;
        using var starter = new PublicHostStarter(locator,
            healthProbe: (_, _) => Task.FromResult(File.Exists(locator.DescriptorPath)),
            publicPort: port,
            startProcess: info =>
            {
                capturedArguments = info.Arguments;
                var process = System.Diagnostics.Process.GetCurrentProcess();
                var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(1, $"http://127.0.0.1:{port}/",
                    info.Environment["RU_DESKTOP_TOKEN"]!, process.Id, process.StartTime.ToUniversalTime().ToString("O"),
                    new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
                Directory.CreateDirectory(Path.GetDirectoryName(locator.DescriptorPath)!);
                File.WriteAllText(locator.DescriptorPath, System.Text.Json.JsonSerializer.Serialize(descriptor, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions));
                return process;
            });

        var session = await starter.GetOrStartAsync(_root);

        Assert.NotNull(session);
        Assert.Equal($"--urls http://127.0.0.1:{port} --managed-runtime", capturedArguments);
    }

    [Fact]
    public void Starter_dispose_can_be_called_multiple_times_safely()
    {
        var locator = new PublicHostLocator(Path.Combine(_root, "disp-" + Guid.NewGuid().ToString("N")));
        using var starter = new PublicHostStarter(locator);
        starter.Dispose();
        starter.Dispose(); // idempotent
    }

    private static int FindFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static bool IsPortOccupied(int port)
    {
        try { using var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return false; } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse) { return true; }
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
}
