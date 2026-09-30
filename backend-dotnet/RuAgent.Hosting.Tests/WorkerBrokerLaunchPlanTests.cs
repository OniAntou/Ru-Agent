using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class WorkerBrokerLaunchPlanTests
{
    [Fact]
    public void Managed_launch_plan_owns_a_single_broker_process()
    {
        using var project = TemporaryProject.Create();
        var startup = project.Startup(18081);

        var plan = WorkerBrokerLaunchPlan.Create(project.Root, startup, project.LogRoot);

        Assert.Equal(new[] { "ru-agent-broker" }, plan.Children.Select(child => child.Role));
        Assert.Equal(18081, plan.BrokerPort);
        Assert.Equal(project.BrokerExecutable, plan.Children[0].FileName);
        Assert.DoesNotContain(plan.Children, child => child.Role == "dotnet-agent-worker");
        Assert.DoesNotContain(plan.Children, child => child.Role.StartsWith("python-", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Children, child => child.Arguments.Contains("ru_runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void Broker_receives_the_descriptor_and_no_provider_credentials()
    {
        using var project = TemporaryProject.Create();
        var plan = WorkerBrokerLaunchPlan.Create(project.Root, project.Startup(18084), project.LogRoot);

        var broker = plan.Children[0].Environment;
        Assert.Equal(plan.BrokerDescriptorPath, broker["RU_AGENT_BROKER_DESCRIPTOR"]);
        Assert.Equal(project.Root, broker["RU_WORKSPACE_ROOT"]);
        Assert.Equal(project.LogRoot, broker["RU_LOG_ROOT"]);
        Assert.DoesNotContain(broker.Keys, key => key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(broker.Keys, key => key.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Startup_rejects_colliding_ports()
    {
        using var project = TemporaryProject.Create();
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, project.Startup(8000), project.LogRoot));
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, project.Startup(8001), project.LogRoot));
    }

    [Fact]
    public void Failed_plan_validation_does_not_publish_a_stale_broker_descriptor()
    {
        using var project = TemporaryProject.Create();
        var startup = project.Startup(18098);
        project.DeleteBroker();

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
            File.WriteAllText(broker, "test");
            return new TemporaryProject(root, logRoot, broker);
        }

        public WorkerBrokerStartupOptions Startup(int brokerPort) =>
            new(brokerPort, BrokerExecutable);

        public void DeleteBroker() => File.Delete(BrokerExecutable);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
