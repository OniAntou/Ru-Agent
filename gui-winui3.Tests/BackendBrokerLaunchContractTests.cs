using System.Text.Json;
using System.Text.Json.Serialization;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendBrokerLaunchContractTests
{
    [Fact]
    public void Launch_plan_owns_broker_descriptor_and_places_broker_before_public_host()
    {
        using var project = TemporaryProject.Create();
        var logRoot = Path.Combine(project.Root, "logs");

        var plan = BackendLaunchPlan.Create(
            project.Root,
            "desktop-secret",
            "worker-secret",
            workerPort: 18085,
            brokerPort: 18086,
            edgeExecutable: project.EdgePath,
            brokerExecutable: project.BrokerPath,
            configuredLogRoot: logRoot);

        Assert.Equal(new[] { "python-runtime-worker", "ru-agent-broker", "aspnetcore-edge" }, plan.Children.Select(child => child.Role));
        Assert.Equal(18086, plan.BrokerPort);
        Assert.Equal(project.BrokerPath, plan.Children[1].FileName);
        Assert.Equal(plan.BrokerDescriptorPath, plan.Children[1].Environment["RU_AGENT_BROKER_DESCRIPTOR"]);
        Assert.Equal(plan.BrokerDescriptorPath, plan.Children[2].Environment["RU_AGENT_BROKER_DESCRIPTOR"]);
        Assert.DoesNotContain("desktop-secret", plan.Children[1].Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("worker-secret", plan.Children[1].Arguments, StringComparison.Ordinal);

        var descriptor = JsonSerializer.Deserialize<BrokerSessionDescriptorContract>(
            File.ReadAllText(plan.BrokerDescriptorPath));
        Assert.NotNull(descriptor);
        Assert.Equal("http://127.0.0.1:18086", descriptor!.BaseUrl);
        Assert.Equal(plan.BrokerSessionRoot, Path.GetFullPath(descriptor.SessionRoot));
        Assert.Equal(1, descriptor.ProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.AuthToken));
    }

    private sealed record BrokerSessionDescriptorContract(
        [property: JsonPropertyName("base_url")] string BaseUrl,
        [property: JsonPropertyName("auth_token")] string AuthToken,
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
        [property: JsonPropertyName("session_root")] string SessionRoot,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string root, string edgePath, string brokerPath)
        {
            Root = root;
            EdgePath = edgePath;
            BrokerPath = brokerPath;
        }

        public string Root { get; }
        public string EdgePath { get; }
        public string BrokerPath { get; }

        public static TemporaryProject Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-broker-plan-" + Guid.NewGuid().ToString("N"));
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
            Directory.CreateDirectory(Path.GetDirectoryName(edgePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(brokerPath)!);
            File.WriteAllText(pythonPath, "test");
            File.WriteAllText(edgePath, "test");
            File.WriteAllText(brokerPath, "test");
            return new TemporaryProject(root, edgePath, brokerPath);
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
