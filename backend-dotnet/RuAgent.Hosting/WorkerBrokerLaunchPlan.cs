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
        int workerPort,
        int brokerPort,
        string brokerDescriptorPath,
        string brokerSessionRoot,
        IReadOnlyList<WorkerBrokerProcessSpec> children)
    {
        WorkerPort = workerPort;
        BrokerPort = brokerPort;
        BrokerDescriptorPath = brokerDescriptorPath;
        BrokerSessionRoot = brokerSessionRoot;
        Children = children;
    }

    public int WorkerPort { get; }
    public int BrokerPort { get; }
    public string BrokerDescriptorPath { get; }
    public string BrokerSessionRoot { get; }
    public IReadOnlyList<WorkerBrokerProcessSpec> Children { get; }

    public static WorkerBrokerLaunchPlan Create(
        string projectRoot,
        string workerSecret,
        int workerPort,
        string? edgeExecutable = null,
        string? configuredLogRoot = null,
        int? brokerPort = null,
        string? brokerExecutable = null)
    {
        // edgeExecutable is ignored for WorkerBroker only (kept for compatibility with moved tests)
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerSecret);
        if (workerPort is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(workerPort));

        const int publicPort = 8000;
        var root = Path.GetFullPath(projectRoot);
        var selectedBrokerPort = brokerPort ?? FindFreeLoopbackPort();
        if (selectedBrokerPort is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(brokerPort));

        if (workerPort == publicPort || workerPort == 8001 || workerPort == selectedBrokerPort)
            throw new ArgumentException("Worker port must differ from public port 8000, VTube Studio 8001, and Broker port.", nameof(workerPort));
        if (selectedBrokerPort == publicPort || selectedBrokerPort == 8001)
            throw new ArgumentException("Broker port must differ from public port 8000 and VTube 8001.", nameof(brokerPort));

        var pythonExecutable = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExecutable))
            pythonExecutable = "python.exe";

        var resolvedLogRoot = ResolveLogRoot(root, configuredLogRoot);
        var bootstrapRoot = Path.Combine(resolvedLogRoot, "bootstrap");
        Directory.CreateDirectory(bootstrapRoot);

        var publishedBroker = brokerExecutable;
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            publishedBroker = Path.Combine(root, "backend-dotnet", "RuAgent.Broker", "bin", "Release", "net10.0", "win-x64", "publish", "RuAgent.Broker.exe");
        }
        publishedBroker = Path.GetFullPath(publishedBroker);
        if (!File.Exists(publishedBroker))
        {
            throw new FileNotFoundException(
                $"Published RuAgent.Broker.exe was not found at '{publishedBroker}'. Run build.bat before selecting Agent mode.",
                publishedBroker);
        }

        var brokerSessionRoot = Path.Combine(resolvedLogRoot, "agent", "session");
        Directory.CreateDirectory(brokerSessionRoot);
        var brokerDescriptorPath = Path.Combine(brokerSessionRoot, "broker.json");
        var brokerDescriptor = new BrokerSessionDescriptor(
            $"http://127.0.0.1:{selectedBrokerPort}",
            GenerateSecret(),
            AgentEnvelopeCodec.ProtocolVersion,
            brokerSessionRoot,
            DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(
            brokerDescriptorPath,
            JsonSerializer.Serialize(brokerDescriptor, BrokerSessionDescriptor.JsonOptions));

        var workerEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RU_WORKER_TOKEN"] = workerSecret,
            ["RU_LOG_ROOT"] = resolvedLogRoot,
        };
        var brokerEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RU_AGENT_BROKER_DESCRIPTOR"] = brokerDescriptorPath,
            ["RU_LOG_ROOT"] = resolvedLogRoot,
        };

        var children = new List<WorkerBrokerProcessSpec>
        {
            new(
                "python-runtime-worker",
                pythonExecutable,
                $"-m ru_runtime.worker.server --host 127.0.0.1 --port {workerPort}",
                root,
                workerEnvironment,
                Path.Combine(bootstrapRoot, "backend_worker_out.jsonl"),
                Path.Combine(bootstrapRoot, "backend_worker_err.jsonl")),
            new(
                "ru-agent-broker",
                publishedBroker,
                string.Empty,
                root,
                brokerEnvironment,
                Path.Combine(bootstrapRoot, "backend_broker_out.jsonl"),
                Path.Combine(bootstrapRoot, "backend_broker_err.jsonl")),
        };

        return new WorkerBrokerLaunchPlan(
            workerPort,
            selectedBrokerPort,
            brokerDescriptorPath,
            brokerSessionRoot,
            children);
    }

    private static string ResolveLogRoot(string projectRoot, string? configuredLogRoot)
    {
        var explicitLogRoot = !string.IsNullOrWhiteSpace(configuredLogRoot)
            ? configuredLogRoot.Trim()
            : Environment.GetEnvironmentVariable("RU_LOG_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitLogRoot))
        {
            var resolved = Path.GetFullPath(explicitLogRoot);
            Directory.CreateDirectory(resolved);
            return resolved;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = projectRoot;

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
