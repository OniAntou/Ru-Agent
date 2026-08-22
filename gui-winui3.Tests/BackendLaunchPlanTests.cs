using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendLaunchPlanTests
{
    [Fact]
    public void StartsPrivateRuntimeWorkerBrokerAndPublicAspNetCoreBackend()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);

        var plan = BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            "worker-secret",
            workerPort: 8005);

        Assert.Equal(8000, plan.PublicPort);
        Assert.Equal(8005, plan.WorkerPort);
        Assert.Equal(new[] { "python-runtime-worker", "ru-agent-broker", "aspnetcore-edge" }, plan.Children.Select(child => child.Role));
        Assert.Equal(
            "-m ru_runtime.worker.v1.server --host 127.0.0.1 --port 8005",
            plan.Children[0].Arguments);
        Assert.Equal(project.BrokerPath, plan.Children[1].FileName);
        Assert.Equal(project.EdgePath, plan.Children[2].FileName);
        Assert.Equal("http://127.0.0.1:8000", plan.Children[2].Environment["RuBackend__BindUrl"]);
        Assert.Equal("ws://127.0.0.1:8005/worker/v1", plan.Children[2].Environment["RuBackend__WorkerUrl"]);
        Assert.Equal("worker-secret", plan.Children[0].Environment["RU_WORKER_TOKEN"]);
        Assert.False(plan.Children[0].Environment.ContainsKey("RU_DESKTOP_TOKEN"));
        Assert.False(plan.Children[1].Environment.ContainsKey("RU_DESKTOP_TOKEN"));
        Assert.False(plan.Children[1].Environment.ContainsKey("RU_WORKER_TOKEN"));
        Assert.Equal("secret-token", plan.Children[2].Environment["RU_DESKTOP_TOKEN"]);
        Assert.Equal("worker-secret", plan.Children[2].Environment["RU_WORKER_TOKEN"]);
        Assert.Equal(plan.BrokerDescriptorPath, plan.Children[1].Environment["RU_AGENT_BROKER_DESCRIPTOR"]);
        Assert.Equal(plan.BrokerDescriptorPath, plan.Children[2].Environment["RU_AGENT_BROKER_DESCRIPTOR"]);
        Assert.True(plan.Children[0].Environment.ContainsKey("RU_LOG_ROOT"));
        Assert.True(plan.Children[1].Environment.ContainsKey("RU_LOG_ROOT"));
        Assert.True(plan.Children[2].Environment.ContainsKey("RU_LOG_ROOT"));
        Assert.Equal(plan.Children[0].Environment["RU_LOG_ROOT"], plan.Children[1].Environment["RU_LOG_ROOT"]);
        Assert.Equal(plan.Children[0].Environment["RU_LOG_ROOT"], plan.Children[2].Environment["RU_LOG_ROOT"]);
        var logRoot = plan.Children[0].Environment["RU_LOG_ROOT"];
        Assert.DoesNotContain(project.Root, logRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(logRoot, "bootstrap", "backend_worker_out.log"), plan.Children[0].StandardOutputLogPath);
        Assert.Equal(Path.Combine(logRoot, "bootstrap", "backend_worker_err.log"), plan.Children[0].StandardErrorLogPath);
        Assert.Equal(Path.Combine(logRoot, "bootstrap", "backend_broker_out.log"), plan.Children[1].StandardOutputLogPath);
        Assert.Equal(Path.Combine(logRoot, "bootstrap", "backend_broker_err.log"), plan.Children[1].StandardErrorLogPath);
        Assert.Equal(Path.Combine(logRoot, "bootstrap", "backend_aspnetcore_out.log"), plan.Children[2].StandardOutputLogPath);
        Assert.Equal(Path.Combine(logRoot, "bootstrap", "backend_aspnetcore_err.log"), plan.Children[2].StandardErrorLogPath);
        Assert.DoesNotContain(
            plan.Children[2].Environment.Keys,
            key => key.Contains("Ownership", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CustomLogRootIsPropagatedAndUsedForBootstrapPaths()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);
        var customLogRoot = Path.Combine(Path.GetTempPath(), "ru-custom-logs-" + Guid.NewGuid().ToString("N"));

        var plan = BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            "worker-secret",
            workerPort: 8005,
            configuredLogRoot: customLogRoot);

        Assert.Equal(Path.GetFullPath(customLogRoot), plan.Children[0].Environment["RU_LOG_ROOT"]);
        Assert.Equal(Path.GetFullPath(customLogRoot), plan.Children[1].Environment["RU_LOG_ROOT"]);
        Assert.Equal(Path.GetFullPath(customLogRoot), plan.Children[2].Environment["RU_LOG_ROOT"]);
        Assert.Equal(Path.Combine(customLogRoot, "bootstrap", "backend_worker_out.log"), plan.Children[0].StandardOutputLogPath);
        Assert.Equal(Path.Combine(customLogRoot, "bootstrap", "backend_broker_out.log"), plan.Children[1].StandardOutputLogPath);
        Assert.Equal(Path.Combine(customLogRoot, "bootstrap", "backend_aspnetcore_out.log"), plan.Children[2].StandardOutputLogPath);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(8001)]
    public void WorkerPortCollidingWithPublicOrVtsPortIsRejected(int invalidPort)
    {
        using var project = TemporaryProject.Create(includePublishedEdge: true);

        Assert.Throws<ArgumentException>(() => BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            "worker-secret",
            workerPort: invalidPort));
    }

    [Fact]
    public void MissingPublishedEdgeIsRejectedBeforeAChildCanStart()
    {
        using var project = TemporaryProject.Create(includePublishedEdge: false);

        var error = Assert.Throws<FileNotFoundException>(() => BackendLaunchPlan.Create(
            project.Root,
            "secret-token",
            "worker-secret",
            workerPort: 8005));

        Assert.Contains("RuBackend.exe", error.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string root, string pythonPath, string edgePath, string brokerPath)
        {
            Root = root;
            PythonPath = pythonPath;
            EdgePath = edgePath;
            BrokerPath = brokerPath;
        }

        public string Root { get; }
        public string PythonPath { get; }
        public string EdgePath { get; }
        public string BrokerPath { get; }

        public static TemporaryProject Create(bool includePublishedEdge)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-backend-plan-" + Guid.NewGuid().ToString("N"));
            var pythonPath = Path.Combine(root, ".venv", "Scripts", "python.exe");
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
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
            File.WriteAllText(pythonPath, "test");
            if (includePublishedEdge)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(edgePath)!);
                File.WriteAllText(edgePath, "test");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(brokerPath)!);
            File.WriteAllText(brokerPath, "test");

            return new TemporaryProject(root, pythonPath, edgePath, brokerPath);
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
