using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendProcessManagerTests
{
    [Fact]
    public async Task AspNetCoreModeAssignsBothChildrenToOneJobAndProbesPublicPort()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);
        var launcher = new FakeProcessFactory();
        var jobs = new FakeJobFactory();
        var readiness = new FakeReadinessProbe(ready: true);
        using var manager = new BackendProcessManager(
            BackendHostMode.AspNetCore,
            workerPort: 8001,
            processFactory: launcher,
            jobFactory: jobs,
            readinessProbe: readiness);

        var started = await manager.StartBackendAsync(project.Root);

        Assert.True(started);
        Assert.True(manager.IsRunning);
        Assert.Equal(new[] { "fastapi-worker", "aspnetcore-edge" }, launcher.Specs.Select(spec => spec.Role));
        Assert.Single(jobs.Created);
        Assert.Equal(2, jobs.Created[0].Assigned.Count);
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
            BackendHostMode.AspNetCore,
            workerPort: 8001,
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
            BackendHostMode.AspNetCore,
            workerPort: 8001,
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
    public async Task FastApiModeRetainsOneChildOnPublicPort()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: false);
        var launcher = new FakeProcessFactory();
        var jobs = new FakeJobFactory();
        var readiness = new FakeReadinessProbe(ready: true);
        using var manager = new BackendProcessManager(
            BackendHostMode.FastApi,
            workerPort: 8001,
            processFactory: launcher,
            jobFactory: jobs,
            readinessProbe: readiness);

        var started = await manager.StartBackendAsync(project.Root);

        Assert.True(started);
        var spec = Assert.Single(launcher.Specs);
        Assert.Equal("fastapi-worker", spec.Role);
        Assert.EndsWith("--port 8000", spec.Arguments, StringComparison.Ordinal);
        Assert.Null(manager.WorkerPort);
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
