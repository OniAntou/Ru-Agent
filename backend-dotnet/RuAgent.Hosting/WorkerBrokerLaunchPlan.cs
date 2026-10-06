using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Protocol;

namespace RuAgent.Hosting;

public sealed record WorkerBrokerProcessSpec(
    string Role,
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutputLogPath,
    string StandardErrorLogPath);

public sealed class WorkerBrokerLaunchPlan
{
    private WorkerBrokerLaunchPlan(
        int brokerPort,
        string brokerDescriptorPath,
        string brokerSessionRoot,
        IReadOnlyList<WorkerBrokerProcessSpec> children)
    {
        BrokerPort = brokerPort;
        BrokerDescriptorPath = brokerDescriptorPath;
        BrokerSessionRoot = brokerSessionRoot;
        Children = children;
    }

    public int BrokerPort { get; }
    public string BrokerDescriptorPath { get; }
    public string BrokerSessionRoot { get; }
    public IReadOnlyList<WorkerBrokerProcessSpec> Children { get; }

    public static WorkerBrokerLaunchPlan Create(
        string projectRoot,
        WorkerBrokerStartupOptions startup,
        string? configuredLogRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(startup);
        startup.Validate();

        var publicPort = startup.PublicPort ?? PublicHostRuntimeOptions.DefaultPublicPort;
        var root = Path.GetFullPath(projectRoot);
        var resolvedLogRoot = ResolveLogRoot(root, configuredLogRoot);
        var brokerSessionRoot = Path.Combine(resolvedLogRoot, "agent", "session");
        var selectedBrokerPort = startup.BrokerPort ?? FindFreeLoopbackPort();
        ValidatePortSet(publicPort, selectedBrokerPort);

        // A distribution puts every executable beside the host; a repository checkout
        // keeps each one under its own project. RuAppLayout resolves both, preferring
        // the flat layout so a packaged install never depends on repository structure.
        var appBaseDirectory = AppContext.BaseDirectory;
        var publishedBroker = startup.BrokerExecutable;
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            publishedBroker = RuAppLayout.ResolveBrokerExecutable(root, appBaseDirectory);
        }
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            throw new FileNotFoundException(
                $"Published {RuAppLayout.BrokerExecutableName} was not found. Expected it beside the host at '{appBaseDirectory}' or under the repository at '{root}'. Run build.bat, or set RU_AGENT_BROKER_EXE.");
        }

        publishedBroker = Path.GetFullPath(publishedBroker);
        if (!File.Exists(publishedBroker))
        {
            throw new FileNotFoundException(
                $"Published {RuAppLayout.BrokerExecutableName} was not found at '{publishedBroker}'. Run build.bat, or set RU_AGENT_BROKER_EXE.");
        }

        Directory.CreateDirectory(brokerSessionRoot);
        var descriptorPath = Path.Combine(brokerSessionRoot, "broker.json");
        var bootstrapRoot = Path.Combine(resolvedLogRoot, "bootstrap");
        Directory.CreateDirectory(bootstrapRoot);
        var children = new List<WorkerBrokerProcessSpec>
        {
            new(
                "ru-agent-broker",
                publishedBroker,
                string.Empty,
                root,
                CreateBrokerEnvironment(descriptorPath, resolvedLogRoot, root),
                Path.Combine(bootstrapRoot, "backend_broker_out.jsonl"),
                Path.Combine(bootstrapRoot, "backend_broker_err.jsonl")),
        };

        var descriptor = new BrokerSessionDescriptor(
            $"http://127.0.0.1:{selectedBrokerPort}",
            GenerateSecret(),
            AgentEnvelopeCodec.ProtocolVersion,
            brokerSessionRoot,
            DateTimeOffset.UtcNow.AddHours(1));
        // The descriptor carries the Broker's bearer token, so it is written owner
        // only. Every other credential writer here protects its file; this one
        // previously inherited whatever the log root grants, which is every local
        // principal whenever RU_LOG_ROOT points outside the user profile.
        OwnerOnlyFile.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));

        return new WorkerBrokerLaunchPlan(selectedBrokerPort, descriptorPath, brokerSessionRoot, children);
    }

    private static void ValidatePortSet(int publicPort, int brokerPort)
    {
        var ports = new[] { publicPort, 8001, brokerPort };
        if (ports.Any(port => port is < 1024 or > 65_535) || ports.Distinct().Count() != ports.Length)
        {
            throw new ArgumentException("Public and Broker ports must be distinct and valid.");
        }
    }

    private static Dictionary<string, string> CreateBrokerEnvironment(string descriptorPath, string logRoot, string workspaceRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RU_AGENT_BROKER_DESCRIPTOR"] = descriptorPath,
            ["RU_LOG_ROOT"] = logRoot,
            ["RU_WORKSPACE_ROOT"] = workspaceRoot,
        };

        // Web options are configured on the Host and forwarded to the Broker
        // process through the environment; unset variables keep web disabled.
        // RU_PROTECTED_PATHS lets the user add paths to the Broker's hard deny
        // list; the Broker reads it directly.
        foreach (var name in (string[])
            [
                "RU_AGENT_BROKER_WEB_ENABLED",
                "RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT",
                "RU_AGENT_BROKER_WEB_MAX_RESPONSE_BYTES",
                "RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL",
                "RU_PROTECTED_PATHS",
            ])
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private static string ResolveLogRoot(string projectRoot, string? configuredLogRoot)
    {
        var explicitLogRoot = !string.IsNullOrWhiteSpace(configuredLogRoot) ? configuredLogRoot.Trim() : Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitLogRoot))
        {
            var resolved = Path.GetFullPath(explicitLogRoot);
            Directory.CreateDirectory(resolved);
            return resolved;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = projectRoot;
        var logRoot = Path.Combine(localAppData, "Ru", "logs");
        Directory.CreateDirectory(logRoot);
        return logRoot;
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
