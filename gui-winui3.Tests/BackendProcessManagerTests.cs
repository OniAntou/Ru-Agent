using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendProcessManagerTests
{
    [Fact]
    public async Task AssignsRuntimeWorkerAndAspNetCoreBackendToOneJobAndProbesPublicPort()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);
        var launcher = new FakeProcessFactory();
        var jobs = new FakeJobFactory();
        var readiness = new FakeReadinessProbe(ready: true);
        using var manager = new BackendProcessManager(
            workerPort: 8005,
            processFactory: launcher,
            jobFactory: jobs,
            readinessProbe: readiness);

        var started = await manager.StartBackendAsync(project.Root);

        Assert.True(started);
        Assert.True(manager.IsRunning);
        Assert.Equal(new[] { "python-runtime-worker", "ru-agent-broker", "aspnetcore-edge" }, launcher.Specs.Select(spec => spec.Role));
        Assert.Single(jobs.Created);
        Assert.Equal(3, jobs.Created[0].Assigned.Count);
        Assert.Equal(8000, readiness.Port);
        Assert.Equal(manager.AuthSecret, readiness.Token);
    }

    [Fact]
    public async Task ReadinessFailureCleansUpEveryStartedChild()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);
        var launcher = new FakeProcessFactory();
        var jobs = new FakeJobFactory();
        var readiness = new FakeReadinessProbe(ready: false);
        using var manager = new BackendProcessManager(
            workerPort: 8005,
            processFactory: launcher,
            jobFactory: jobs,
            readinessProbe: readiness);

        var started = await manager.StartBackendAsync(project.Root);

        Assert.False(started);
        Assert.False(manager.IsRunning);
        Assert.All(launcher.Handles, handle => Assert.True(handle.KillCount > 0));
        Assert.True(jobs.Created[0].Disposed);
        Assert.Contains("ready", manager.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingPublishedEdgeDoesNotStartAChild()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: false);
        var launcher = new FakeProcessFactory();
        var jobs = new FakeJobFactory();
        var readiness = new FakeReadinessProbe(ready: true);
        using var manager = new BackendProcessManager(
            workerPort: 8005,
            processFactory: launcher,
            jobFactory: jobs,
            readinessProbe: readiness);

        var started = await manager.StartBackendAsync(project.Root);

        Assert.False(started);
        Assert.Empty(launcher.Specs);
        Assert.Contains("RuBackend.exe", manager.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(jobs.Created);
    }

    [Fact]
    public void DefaultsWorkerPortTo8005WhenEnvVarUnset()
    {
        var original = Environment.GetEnvironmentVariable("RU_BACKEND_WORKER_PORT");
        try
        {
            Environment.SetEnvironmentVariable("RU_BACKEND_WORKER_PORT", null);
            using var manager = new BackendProcessManager();
            Assert.Equal(8005, manager.WorkerPort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_BACKEND_WORKER_PORT", original);
        }
    }

    [Theory]
    [InlineData("8000")]
    [InlineData("8001")]
    [InlineData("79999")]
    [InlineData("not-a-number")]
    public void RejectsInvalidOrCollidingWorkerPortEnvVar(string invalidValue)
    {
        var original = Environment.GetEnvironmentVariable("RU_BACKEND_WORKER_PORT");
        try
        {
            Environment.SetEnvironmentVariable("RU_BACKEND_WORKER_PORT", invalidValue);
            Assert.ThrowsAny<ArgumentException>(() => new BackendProcessManager());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_BACKEND_WORKER_PORT", original);
        }
    }

    private sealed class FakeProcessFactory : IBackendProcessFactory
    {
        public List<BackendProcessSpec> Specs { get; } = new();
        public List<FakeProcessHandle> Handles { get; } = new();

        public IBackendProcessHandle Start(BackendProcessSpec spec)
        {
            Specs.Add(spec);
            var handle = new FakeProcessHandle();
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class FakeJobFactory : IBackendJobFactory
    {
        public List<FakeJob> Created { get; } = new();

        public IBackendJob Create()
        {
            var job = new FakeJob();
            Created.Add(job);
            return job;
        }
    }

    private sealed class FakeJob : IBackendJob
    {
        public List<IBackendProcessHandle> Assigned { get; } = new();
        public bool Disposed { get; private set; }

        public void Assign(IBackendProcessHandle process) => Assigned.Add(process);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeReadinessProbe : IBackendReadinessProbe
    {
        private readonly bool _ready;

        public FakeReadinessProbe(bool ready)
        {
            _ready = ready;
        }

        public int Port { get; private set; }
        public string Token { get; private set; } = string.Empty;

        public Task<bool> WaitUntilReadyAsync(int publicPort, string token, CancellationToken cancellationToken)
        {
            Port = publicPort;
            Token = token;
            return Task.FromResult(_ready);
        }
    }

    private sealed class FakeProcessHandle : IBackendProcessHandle
    {
        public IntPtr Handle => IntPtr.Zero;
        public bool HasExited { get; private set; }
        public int KillCount { get; private set; }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public bool WaitForExit(TimeSpan timeout) => HasExited;

        public void Dispose() => HasExited = true;
    }

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static TemporaryProject Create(bool includePublishedEdge)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-backend-manager-" + Guid.NewGuid().ToString("N"));
            var pythonPath = Path.Combine(root, ".venv", "Scripts", "python.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
            File.WriteAllText(pythonPath, "test");
            if (includePublishedEdge)
            {
                var edgePath = Path.Combine(
                    root,
                    "backend-dotnet",
                    "RuBackend",
                    "bin",
                    "Release",
                    "net10.0",
                    "win-x64",
                    "publish",
                    "RuBackend.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(edgePath)!);
                File.WriteAllText(edgePath, "test");

            }

            var brokerPath = Path.Combine(
                root,
                "backend-dotnet",
                "RuAgent.Broker",
                "bin",
                "Release",
                "net10.0",
                "win-x64",
                "publish",
                "RuAgent.Broker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(brokerPath)!);
            File.WriteAllText(brokerPath, "test");

            return new TemporaryProject(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
