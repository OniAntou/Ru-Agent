using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Protocol;

namespace RuApp.Services;

public sealed record BackendProcessSpec(
    string Role,
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutputLogPath,
    string StandardErrorLogPath);

public sealed class BackendLaunchPlan
{
    private BackendLaunchPlan(
        int publicPort,
        int workerPort,
        int brokerPort,
        string brokerDescriptorPath,
        string brokerSessionRoot,
        IReadOnlyList<BackendProcessSpec> children)
    {
        PublicPort = publicPort;
        WorkerPort = workerPort;
        BrokerPort = brokerPort;
        BrokerDescriptorPath = brokerDescriptorPath;
        BrokerSessionRoot = brokerSessionRoot;
        Children = children;
    }

    public int PublicPort { get; }
    public int WorkerPort { get; }
    public int BrokerPort { get; }
    public string BrokerDescriptorPath { get; }
    public string BrokerSessionRoot { get; }
    public IReadOnlyList<BackendProcessSpec> Children { get; }

    public static BackendLaunchPlan Create(
        string projectRoot,
        string authSecret,
        string workerSecret,
        int workerPort,
        string? edgeExecutable = null,
        string? configuredLogRoot = null,
        int? brokerPort = null,
        string? brokerExecutable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(authSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerSecret);

        const int publicPort = 8000;
        if (workerPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(workerPort));
        }

        var root = Path.GetFullPath(projectRoot);
        var selectedBrokerPort = brokerPort ?? FindFreeLoopbackPort();
        if (selectedBrokerPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(brokerPort));
        }

        if (workerPort == publicPort || workerPort == 8001 || workerPort == selectedBrokerPort)
        {
            throw new ArgumentException(
                "The ASP.NET Core worker port must differ from public port 8000, VTube Studio port 8001, and Broker port.",
                nameof(workerPort));
        }

        if (selectedBrokerPort == publicPort || selectedBrokerPort == 8001)
        {
            throw new ArgumentException(
                "The RuAgentBroker port must differ from public port 8000 and VTube Studio port 8001.",
                nameof(brokerPort));
        }

        var pythonExecutable = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExecutable))
        {
            pythonExecutable = "python.exe";
        }

        var resolvedLogRoot = ResolveLogRoot(root, configuredLogRoot);
        var bootstrapRoot = Path.Combine(resolvedLogRoot, "bootstrap");
        Directory.CreateDirectory(bootstrapRoot);

        var publishedEdge = edgeExecutable;
        if (string.IsNullOrWhiteSpace(publishedEdge))
        {
            publishedEdge = Path.Combine(
                root,
                "backend-dotnet",
                "RuBackend",
                "bin",
                "Release",
                "net10.0",
                "win-x64",
                "publish",
                "RuBackend.exe");
        }

        publishedEdge = Path.GetFullPath(publishedEdge);
        if (!File.Exists(publishedEdge))
        {
            throw new FileNotFoundException(
                $"Published RuBackend.exe was not found at '{publishedEdge}'. Run build.bat before selecting ASP.NET Core mode.",
                publishedEdge);
        }

        var publishedBroker = brokerExecutable;
        if (string.IsNullOrWhiteSpace(publishedBroker))
        {
            publishedBroker = Path.Combine(
                root,
                "backend-dotnet",
                "RuAgent.Broker",
                "bin",
                "Release",
                "net10.0",
                "win-x64",
                "publish",
                "RuAgent.Broker.exe");
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
        var edgeEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RU_DESKTOP_TOKEN"] = authSecret,
            ["RU_WORKER_TOKEN"] = workerSecret,
            ["RU_AGENT_BROKER_DESCRIPTOR"] = brokerDescriptorPath,
            ["RU_LOG_ROOT"] = resolvedLogRoot,
            ["RuBackend__BindUrl"] = $"http://127.0.0.1:{publicPort}",
            ["RuBackend__WorkerUrl"] = $"ws://127.0.0.1:{workerPort}/worker/v1",
        };
        var children = new List<BackendProcessSpec>
        {
            new(
                "python-runtime-worker",
                pythonExecutable,
                $"-m ru_runtime.worker.v1.server --host 127.0.0.1 --port {workerPort}",
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
            new(
                "aspnetcore-edge",
                publishedEdge,
                $"--urls http://127.0.0.1:{publicPort}",
                root,
                edgeEnvironment,
                Path.Combine(bootstrapRoot, "backend_aspnetcore_out.jsonl"),
                Path.Combine(bootstrapRoot, "backend_aspnetcore_err.jsonl")),
        };

        return new BackendLaunchPlan(
            publicPort,
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
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = projectRoot;
        }

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
