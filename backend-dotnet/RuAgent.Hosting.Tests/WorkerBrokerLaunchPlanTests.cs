using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class WorkerBrokerLaunchPlanTests
{
    [Fact]
    public void LaunchPlanContainsWorkerAndBrokerOnly()
    {
        using var project = TemporaryProject.Create(includePublishedBroker: true);
        var plan = WorkerBrokerLaunchPlan.Create(project.Root, "worker-secret-" + Guid.NewGuid().ToString("N"), workerPort: 18080, brokerPort: 18081, configuredLogRoot: project.LogRoot);

        Assert.Equal(2, plan.Children.Count);
        Assert.Equal(new[] { "python-runtime-worker", "ru-agent-broker" }, plan.Children.Select(c => c.Role));
        Assert.DoesNotContain(plan.Children, c => c.Role == "aspnetcore-edge");
        Assert.DoesNotContain(plan.Children, c => c.Role == "public-host");
    }

    [Fact]
    public void NeverContainsPublicHost()
    {
        using var project = TemporaryProject.Create(includePublishedBroker: true);
        var plan = WorkerBrokerLaunchPlan.Create(project.Root, "s", 18082, brokerPort: 18083, configuredLogRoot: project.LogRoot);
        Assert.All(plan.Children, c => Assert.NotEqual("aspnetcore-edge", c.Role));
    }

    [Fact]
    public void EnvIncludesPrivateChildTokensButNoProviderSecrets()
    {
        using var project = TemporaryProject.Create(includePublishedBroker: true);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "must-not-inherit");
        Environment.SetEnvironmentVariable("RU_DESKTOP_TOKEN", "must-not-inherit");
        try
        {
            var workerSecret = "worker-" + Guid.NewGuid().ToString("N");
            var plan = WorkerBrokerLaunchPlan.Create(project.Root, workerSecret, 18084, brokerPort: 18085, configuredLogRoot: project.LogRoot);
            var workerEnv = plan.Children[0].Environment;
            var brokerEnv = plan.Children[1].Environment;

            Assert.Equal(workerSecret, workerEnv["RU_WORKER_TOKEN"]);
            Assert.False(workerEnv.ContainsKey("RU_DESKTOP_TOKEN"));
            Assert.False(brokerEnv.ContainsKey("RU_DESKTOP_TOKEN"));
            Assert.False(brokerEnv.ContainsKey("RU_WORKER_TOKEN"));
            Assert.DoesNotContain(workerEnv.Keys, k => k.Contains("OPENAI", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(brokerEnv.Keys, k => k.Contains("OPENAI", StringComparison.OrdinalIgnoreCase));
            Assert.True(workerEnv.ContainsKey("RU_LOG_ROOT"));
            Assert.True(brokerEnv.ContainsKey("RU_LOG_ROOT"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("RU_DESKTOP_TOKEN", null);
        }
    }

    [Fact]
    public void BootstrapSummariesAreBoundedMetadataOnly()
    {
        using var project = TemporaryProject.Create(includePublishedBroker: true);
        var plan = WorkerBrokerLaunchPlan.Create(project.Root, "s", 18086, brokerPort: 18087, configuredLogRoot: project.LogRoot);
        // Bootstrap paths are under logRoot/bootstrap and are .jsonl summaries, not raw output
        foreach (var child in plan.Children)
        {
            Assert.EndsWith(".jsonl", child.StandardOutputLogPath, StringComparison.Ordinal);
            Assert.EndsWith(".jsonl", child.StandardErrorLogPath, StringComparison.Ordinal);
            Assert.Contains("bootstrap", child.StandardOutputLogPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkerPortCollisionIsRejected()
    {
        using var project = TemporaryProject.Create(includePublishedBroker: true);
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, "s", 8000, brokerPort: 18088, configuredLogRoot: project.LogRoot));
        Assert.Throws<ArgumentException>(() => WorkerBrokerLaunchPlan.Create(project.Root, "s", 8001, brokerPort: 18089, configuredLogRoot: project.LogRoot));
    }

    private sealed class TemporaryProject : IDisposable
    {
        public string Root { get; }
        public string LogRoot { get; }
        private TemporaryProject(string root, string logRoot) { Root = root; LogRoot = logRoot; }
        public static TemporaryProject Create(bool includePublishedBroker)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-host-plan-" + Guid.NewGuid().ToString("N"));
            var logRoot = Path.Combine(root, "logs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(logRoot);
            var pythonPath = Path.Combine(root, ".venv", "Scripts", "python.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
            File.WriteAllText(pythonPath, "test");
            if (includePublishedBroker)
            {
                var brokerPath = Path.Combine(root, "backend-dotnet", "RuAgent.Broker", "bin", "Release", "net10.0", "win-x64", "publish", "RuAgent.Broker.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(brokerPath)!);
                File.WriteAllText(brokerPath, "test");
            }
            return new TemporaryProject(root, logRoot);
        }
        public void Dispose() { try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { } }
    }
}
