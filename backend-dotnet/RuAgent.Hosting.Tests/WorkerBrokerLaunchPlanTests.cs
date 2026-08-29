using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class WorkerBrokerLaunchPlanTests
{
    [Fact]
    public void Dotnet_mode_owns_agent_device_and_broker_in_order()
    {
        using var project = TemporaryProject.Create();
        var startup = project.DotnetStartup(18080, 18081, 18082);

        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        Assert.Equal(new[] { "dotnet-agent-worker", "python-device-worker", "ru-agent-broker" }, plan.Children.Select(child => child.Role));
        Assert.Equal(18080, plan.AgentWorkerPort);
        Assert.Equal(18081, plan.DeviceWorkerPort);
        Assert.Equal(18082, plan.BrokerPort);
        Assert.Contains("--host 127.0.0.1 --port 18080", plan.Children[0].Arguments, StringComparison.Ordinal);
        Assert.Contains("--v1-only", plan.Children[1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotnet_worker_never_receives_device_token_and_python_never_receives_provider_bootstrap()
    {
        using var project = TemporaryProject.Create();
        var startup = project.DotnetStartup(18083, 18084, 18085);
        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        var agent = plan.Children[0].Environment;
        var device = plan.Children[1].Environment;
        var broker = plan.Children[2].Environment;
        Assert.Equal(startup.AgentWorkerToken, agent["RU_AGENT_WORKER_TOKEN"]);
        Assert.Equal(startup.ProviderBootstrapPath, agent["RU_AGENT_PROVIDER_BOOTSTRAP"]);
        Assert.False(agent.ContainsKey("RU_WORKER_TOKEN"));
        Assert.Equal(startup.DeviceWorkerToken, device["RU_WORKER_TOKEN"]);
        Assert.False(device.ContainsKey("RU_AGENT_WORKER_TOKEN"));
        Assert.False(device.ContainsKey("RU_AGENT_PROVIDER_BOOTSTRAP"));
        Assert.DoesNotContain(broker.Keys, key => key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(device.Keys, key => key.Contains("OPENAI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Python_compatibility_mode_does_not_start_the_CSharp_worker()
    {
        using var project = TemporaryProject.Create();
        var startup = new WorkerBrokerStartupOptions(
            RuntimeMigrationMode.PythonCompatibility,
            "agent-token",
            "device-token",
            string.Empty,
            18086,
            18087,
            18088,
            project.BrokerExecutable);

        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        Assert.Equal("python-agent-compat-worker", plan.Children[0].Role);
        Assert.DoesNotContain(plan.Children, child => child.Role == "dotnet-agent-worker");
        Assert.Contains("ru_runtime.worker.server", plan.Children[0].Arguments, StringComparison.Ordinal);
        Assert.Contains("--v1-only", plan.Children[1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_rejects_colliding_ports_and_bootstrap_escape()
    {
        using var project = TemporaryProject.Create();
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, project.DotnetStartup(8000, 18090, 18091), project.LogRoot));
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, project.DotnetStartup(18092, 18092, 18093), project.LogRoot));

        var outside = Path.Combine(Path.GetTempPath(), "ru-bootstrap-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret");
        try
        {
            var escaped = project.DotnetStartup(18094, 18095, 18096) with { ProviderBootstrapPath = outside };
            Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, escaped, project.LogRoot));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Failed_plan_validation_does_not_publish_a_stale_broker_descriptor()
    {
        using var project = TemporaryProject.Create();
        var startup = project.DotnetStartup(18097, 18098, 18099);
        project.DeleteAgentWorker();

        Assert.Throws<FileNotFoundException>(() =>
            WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot));

        Assert.False(File.Exists(Path.Combine(project.LogRoot, "agent", "session", "broker.json")));
    }

    private sealed class TemporaryProject : IDisposable
    {
        public string Root { get; }
        public string LogRoot { get; }
        public string BrokerExecutable { get; }
        private TemporaryProject(string root, string logRoot, string brokerExecutable)
        {
            Root = root;
            LogRoot = logRoot;
            BrokerExecutable = brokerExecutable;
        }

        public static TemporaryProject Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-host-plan-" + Guid.NewGuid().ToString("N"));
            var logRoot = Path.Combine(root, "logs");
            Directory.CreateDirectory(Path.Combine(root, ".venv", "Scripts"));
            Directory.CreateDirectory(logRoot);
            File.WriteAllText(Path.Combine(root, ".venv", "Scripts", "python.exe"), "test");
            var broker = Path.Combine(root, "broker.exe");
            var worker = Path.Combine(root, "backend-dotnet", "RuAgent.Runtime.Worker", "bin", "Release", "net10.0", "win-x64", "publish", "RuAgent.Runtime.Worker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
            File.WriteAllText(broker, "test");
            File.WriteAllText(worker, "test");
            return new TemporaryProject(root, logRoot, broker);
        }

        public WorkerBrokerStartupOptions DotnetStartup(int agentPort, int devicePort, int brokerPort)
        {
            var sessionRoot = Path.Combine(LogRoot, "agent", "session");
            Directory.CreateDirectory(sessionRoot);
            var bootstrap = Path.Combine(sessionRoot, "provider-bootstrap.bin");
            File.WriteAllText(bootstrap, "protected");
            return new WorkerBrokerStartupOptions(
                RuntimeMigrationMode.Dotnet,
                "agent-token-" + Guid.NewGuid().ToString("N"),
                "device-token-" + Guid.NewGuid().ToString("N"),
                bootstrap,
                agentPort,
                devicePort,
                brokerPort,
                BrokerExecutable);
        }

        public void DeleteAgentWorker()
        {
            var worker = Path.Combine(
                Root,
                "backend-dotnet",
                "RuAgent.Runtime.Worker",
                "bin",
                "Release",
                "net10.0",
                "win-x64",
                "publish",
                "RuAgent.Runtime.Worker.exe");
            File.Delete(worker);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
