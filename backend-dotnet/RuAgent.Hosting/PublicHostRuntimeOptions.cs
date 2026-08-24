using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RuAgent.Hosting;

public sealed class PublicHostRuntimeOptions
{
    public const int DefaultPublicPort = 8000;
    public const int DefaultWorkerPort = 8005;
    public const int DefaultBrokerPortRangeStart = 1024;
    public const int DefaultBrokerPortRangeEnd = 65535;
    public const string DefaultBindUrl = "http://127.0.0.1:8000";
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    public int PublicPort { get; init; } = DefaultPublicPort;
    public int WorkerPort { get; init; } = DefaultWorkerPort;
    public int BrokerPort { get; init; }
    public string ProjectRoot { get; init; } = Directory.GetCurrentDirectory();
    public string DataRoot { get; init; } = string.Empty;
    public string LogRoot { get; init; } = string.Empty;
    public string DescriptorPath { get; init; } = string.Empty;
    public string InstanceNonce { get; init; } = GenerateNonce();

    public static PublicHostRuntimeOptions Create(
        string projectRoot,
        string? dataRoot = null,
        string? logRoot = null,
        int publicPort = DefaultPublicPort,
        int workerPort = DefaultWorkerPort,
        int? brokerPort = null,
        string? instanceNonce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (publicPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(publicPort));
        if (workerPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(workerPort));
        if (publicPort == workerPort || publicPort == 8001 || workerPort == 8001)
            throw new ArgumentException("Ports must not collide with 8000 or 8001.");
        var resolvedProjectRoot = Path.GetFullPath(projectRoot);
        var resolvedDataRoot = string.IsNullOrWhiteSpace(dataRoot) ? string.Empty : Path.GetFullPath(dataRoot.Trim());
        var resolvedLogRoot = string.IsNullOrWhiteSpace(logRoot) ? ResolveLogRoot(resolvedProjectRoot, null) : Path.GetFullPath(logRoot.Trim());
        var descriptorPath = ResolveDescriptorPath(resolvedDataRoot, resolvedLogRoot, resolvedProjectRoot);
        var selectedBrokerPort = brokerPort ?? FindFreeLoopbackPort();
        if (selectedBrokerPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(brokerPort));
        if (selectedBrokerPort == publicPort || selectedBrokerPort == workerPort || selectedBrokerPort == 8001)
            throw new ArgumentException("Broker port must not collide.");

        return new PublicHostRuntimeOptions
        {
            PublicPort = publicPort,
            WorkerPort = workerPort,
            BrokerPort = selectedBrokerPort,
            ProjectRoot = resolvedProjectRoot,
            DataRoot = resolvedDataRoot,
            LogRoot = resolvedLogRoot,
            DescriptorPath = descriptorPath,
            InstanceNonce = string.IsNullOrWhiteSpace(instanceNonce) ? GenerateNonce() : instanceNonce!,
        };
    }

    public static string ResolveDescriptorPath(string? dataRoot, string? logRoot, string? projectRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(dataRoot)
            ? (string.IsNullOrWhiteSpace(logRoot) ? GetDefaultLocalAppDataRoot(projectRoot) : Path.GetFullPath(logRoot))
            : Path.GetFullPath(dataRoot.Trim());
        // Descriptor lives under business data root; if dataRoot isolated, use it directly.
        // Otherwise use LocalAppData\Ru\runtime_descriptor.json
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            return Path.Combine(root, "runtime_descriptor.json");
        }
        // When logRoot is explicit but dataRoot not, fallback to logRoot sibling? Use default business root
        var businessRoot = GetDefaultBusinessRoot(projectRoot);
        if (!string.IsNullOrWhiteSpace(dataRoot))
            return Path.Combine(Path.GetFullPath(dataRoot), "runtime_descriptor.json");
        return Path.Combine(businessRoot, "runtime_descriptor.json");
    }

    public static string ResolveLogRoot(string projectRoot, string? configuredLogRoot)
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

    public static string GetDefaultBusinessRoot(string? projectRoot)
    {
        var dataRootEnv = Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(dataRootEnv))
            return Path.GetFullPath(dataRootEnv.Trim());
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = projectRoot ?? Directory.GetCurrentDirectory();
        return Path.Combine(localAppData, "Ru");
    }

    public static string GetDefaultLocalAppDataRoot(string? projectRoot)
    {
        return GetDefaultBusinessRoot(projectRoot);
    }

    public static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string GenerateNonce()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string GenerateBearerToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
