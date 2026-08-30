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
        int agentWorkerPort,
        int deviceWorkerPort,
        int brokerPort,
        string brokerDescriptorPath,
        string brokerSessionRoot,
        IReadOnlyList<WorkerBrokerProcessSpec> children)
    {
        AgentWorkerPort = agentWorkerPort;
        DeviceWorkerPort = deviceWorkerPort;
        BrokerPort = brokerPort;
        BrokerDescriptorPath = brokerDescriptorPath;
        BrokerSessionRoot = brokerSessionRoot;
        Children = children;
    }

    public int AgentWorkerPort { get; }
    public int DeviceWorkerPort { get; }
    public int WorkerPort => AgentWorkerPort;
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

        const int publicPort = 8000;
        var root = Path.GetFullPath(projectRoot);
        var resolvedLogRoot = ResolveLogRoot(root, configuredLogRoot);
        var brokerSessionRoot = Path.Combine(resolvedLogRoot, "agent", "session");
        var selectedBrokerPort = startup.BrokerPort ?? FindFreeLoopbackPort();
        ValidatePortSet(publicPort, startup.AgentWorkerPort, startup.DeviceWorkerPort, selectedBrokerPort);

        var publishedBroker = startup.BrokerExecutable;
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            publishedBroker = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE");
        }
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            publishedBroker = Path.Combine(root, "backend-dotnet", "RuAgent.Broker", "bin", "Release", "net10.0", "win-x64", "publish", "RuAgent.Broker.exe");
        }

        publishedBroker = Path.GetFullPath(publishedBroker);
        if (!File.Exists(publishedBroker))
        {
            throw new FileNotFoundException($"Published RuAgent.Broker.exe was not found at '{publishedBroker}'. Run build.bat before selecting Agent mode.", publishedBroker);
        }

        var workerExecutable = Path.Combine(root, "backend-dotnet", "RuAgent.Runtime.Worker", "bin", "Release", "net10.0", "win-x64", "publish", "RuAgent.Runtime.Worker.exe");
        if (!File.Exists(workerExecutable))
        {
            throw new FileNotFoundException($"Published RuAgent.Runtime.Worker.exe was not found at '{workerExecutable}'. Run build.bat before selecting Agent mode.", workerExecutable);
        }

        var providerBootstrapPath = Path.GetFullPath(startup.ProviderBootstrapPath);
        if (!File.Exists(providerBootstrapPath)
            || !IsOwnedPath(brokerSessionRoot, providerBootstrapPath))
        {
            throw new ArgumentException("Provider bootstrap path must be an existing file below the owned session root.", nameof(startup));
        }

        Directory.CreateDirectory(brokerSessionRoot);
        var descriptorPath = Path.Combine(brokerSessionRoot, "broker.json");
        var bootstrapRoot = Path.Combine(resolvedLogRoot, "bootstrap");
        Directory.CreateDirectory(bootstrapRoot);
        var children = new List<WorkerBrokerProcessSpec>();
        children.Add(new WorkerBrokerProcessSpec(
            "dotnet-agent-worker",
            workerExecutable,
            $"--host 127.0.0.1 --port {startup.AgentWorkerPort} --device-port {startup.DeviceWorkerPort}",
            root,
            CreateWorkerEnvironment(
                ("RU_AGENT_WORKER_TOKEN", startup.AgentWorkerToken),
                ("RU_DEVICE_WORKER_TOKEN", startup.DeviceWorkerToken),
                ("RU_AGENT_PROVIDER_BOOTSTRAP", providerBootstrapPath),
                ("RU_PROJECT_ROOT", root),
                ("RU_LOG_ROOT", resolvedLogRoot)),
            Path.Combine(bootstrapRoot, "agent_worker_out.jsonl"),
            Path.Combine(bootstrapRoot, "agent_worker_err.jsonl")));

        children.Add(new WorkerBrokerProcessSpec(
            "ru-agent-broker",
            publishedBroker,
            string.Empty,
            root,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RU_AGENT_BROKER_DESCRIPTOR"] = descriptorPath,
                    ["RU_LOG_ROOT"] = resolvedLogRoot,
                },
            Path.Combine(bootstrapRoot, "backend_broker_out.jsonl"),
            Path.Combine(bootstrapRoot, "backend_broker_err.jsonl")));

        var descriptor = new BrokerSessionDescriptor(
            $"http://127.0.0.1:{selectedBrokerPort}",
            GenerateSecret(),
            AgentEnvelopeCodec.ProtocolVersion,
            brokerSessionRoot,
            DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));

        return new WorkerBrokerLaunchPlan(startup.AgentWorkerPort, startup.DeviceWorkerPort, selectedBrokerPort, descriptorPath, brokerSessionRoot, children);
    }

    private static void ValidatePortSet(int publicPort, int agentPort, int devicePort, int brokerPort)
    {
        var ports = new[] { publicPort, 8001, agentPort, devicePort, brokerPort };
        if (ports.Any(port => port is < 1024 or > 65_535) || ports.Distinct().Count() != ports.Length)
        {
            throw new ArgumentException("Public, worker, device, VTube Studio, and Broker ports must be distinct and valid.");
        }
    }

    private static bool IsOwnedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0);
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

    private static Dictionary<string, string> CreateWorkerEnvironment(
        params (string Key, string Value)[] entries)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            environment[key] = value;
        }

        var configuredDataRoot = Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredDataRoot))
        {
            environment["RU_DATA_ROOT"] = Path.GetFullPath(configuredDataRoot.Trim());
        }

        return environment;
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
