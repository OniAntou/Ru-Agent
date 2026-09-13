using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class WorkerBrokerLaunchPlanTests
{
    [Fact]
    public void Dotnet_mode_owns_the_worker_and_broker_in_one_managed_process()
    {
        using var project = TemporaryProject.Create();
        var startup = project.DotnetStartup(18080, 18081);

        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        Assert.Equal(new[] { "dotnet-agent-worker", "ru-agent-broker" }, plan.Children.Select(child => child.Role));
        Assert.Equal(18080, plan.AgentWorkerPort);
        Assert.Equal(18081, plan.BrokerPort);
        Assert.Contains("--host 127.0.0.1 --port 18080", plan.Children[0].Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--device-port", plan.Children[0].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_worker_receives_the_private_token_but_no_python_device_child()
    {
        using var project = TemporaryProject.Create();
        var startup = project.DotnetStartup(18083, 18084);
        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        var agent = plan.Children[0].Environment;
        var broker = plan.Children[1].Environment;
        Assert.Equal(startup.AgentWorkerToken, agent["RU_AGENT_WORKER_TOKEN"]);
        Assert.Equal(startup.ProviderBootstrapPath, agent["RU_AGENT_PROVIDER_BOOTSTRAP"]);
        Assert.Equal(project.Root, agent["RU_PROJECT_ROOT"]);
        Assert.Equal(project.LogRoot, agent["RU_LOG_ROOT"]);
        Assert.False(agent.ContainsKey("RU_WORKER_TOKEN"));
        Assert.False(agent.ContainsKey("RU_DEVICE_WORKER_TOKEN"));
        Assert.DoesNotContain(broker.Keys, key => key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Children, child => child.Role == "python-device-worker");
    }

    [Fact]
    public void Configured_data_root_is_passed_to_worker_children_for_isolated_runtime_state()
    {
        using var project = TemporaryProject.Create();
        var previous = Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        var dataRoot = Path.Combine(project.Root, "isolated-data");
        Environment.SetEnvironmentVariable("RU_DATA_ROOT", dataRoot);
        try
        {
            var dotnetPlan = WorkerBrokerLaunchPlan.Create(
                project.Root,
                project.DotnetStartup(18100, 18101),
                project.LogRoot);
            Assert.Equal(Path.GetFullPath(dataRoot), dotnetPlan.Children[0].Environment["RU_DATA_ROOT"]);

            Assert.DoesNotContain(
                dotnetPlan.Children,
                child => child.Environment.ContainsKey("RU_WORKER_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_DATA_ROOT", previous);
        }
    }

    [Fact]
    public void Managed_launch_plan_contains_no_legacy_python_process()
    {
        using var project = TemporaryProject.Create();
        var startup = project.DotnetStartup(18086, 18087);

        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        Assert.Equal("dotnet-agent-worker", plan.Children[0].Role);
        Assert.DoesNotContain(plan.Children, child => child.Role.StartsWith("python-", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Children, child => child.Arguments.Contains("ru_runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_rejects_colliding_ports_and_bootstrap_escape()
    {
        using var project = TemporaryProject.Create();
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, project.DotnetStartup(8000, 18090), project.LogRoot));
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, project.DotnetStartup(18092, 18092), project.LogRoot));

        var outside = Path.Combine(Path.GetTempPath(), "ru-bootstrap-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret");
        try
        {
            var escaped = project.DotnetStartup(18094, 18095) with { ProviderBootstrapPath = outside };
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
        var startup = project.DotnetStartup(18097, 18098);
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
            Directory.CreateDirectory(logRoot);
            var broker = Path.Combine(root, "broker.exe");
            var worker = Path.Combine(root, "backend-dotnet", "RuAgent.Runtime.Worker", "bin", "Release", "net10.0", "win-x64", "publish", "RuAgent.Runtime.Worker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
            File.WriteAllText(broker, "test");
            File.WriteAllText(worker, "test");
            return new TemporaryProject(root, logRoot, broker);
        }

        public WorkerBrokerStartupOptions DotnetStartup(int agentPort, int brokerPort)
        {
            var sessionRoot = Path.Combine(LogRoot, "agent", "session");
            Directory.CreateDirectory(sessionRoot);
            var bootstrap = Path.Combine(sessionRoot, "provider-bootstrap.bin");
            File.WriteAllText(bootstrap, "protected");
            return new WorkerBrokerStartupOptions(
                "agent-token-" + Guid.NewGuid().ToString("N"),
                bootstrap,
                agentPort,
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
