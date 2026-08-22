using System.Diagnostics;
using System.Security.Cryptography;

namespace RuApp.Services;

public class BackendProcessManager : IDisposable
{
    private readonly object _gate = new();
    private readonly IBackendProcessFactory _processFactory;
    private readonly IBackendJobFactory _jobFactory;
    private readonly IBackendReadinessProbe _readinessProbe;
    private readonly HttpClient _httpClient;
    private List<IBackendProcessHandle> _children = new();
    private IBackendJob? _job;
    private string? _brokerDescriptorPath;
    private string? _brokerSessionRoot;
    private bool _disposed;

    public event EventHandler? BackendExited;

    public BackendProcessManager(
        int? workerPort = null,
        int? brokerPort = null,
        IBackendProcessFactory? processFactory = null,
        IBackendJobFactory? jobFactory = null,
        IBackendReadinessProbe? readinessProbe = null)
    {
        Port = 8000;
        WorkerPort = workerPort ?? ParseWorkerPort();
        BrokerPort = brokerPort ?? ParseBrokerPort();
        AuthSecret = GenerateSecret();
        WorkerSecret = GenerateSecret();
        _processFactory = processFactory ?? new WindowsBackendProcessFactory();
        _jobFactory = jobFactory ?? new WindowsBackendJobFactory();
        _httpClient = new HttpClient();
        _readinessProbe = readinessProbe ?? new HttpBackendReadinessProbe(_httpClient);
    }

    public string AuthSecret { get; }
    public string WorkerSecret { get; }
    public int Port { get; }
    public int WorkerPort { get; }
    public int BrokerPort { get; private set; }
    public string? BrokerDescriptorPath { get; private set; }
    public string? BrokerSessionRoot { get; private set; }
    public string DisplayName => "ASP.NET Core + RuAgentBroker";
    public string? LastError { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _children.Count > 0 && _children.All(child => !child.HasExited);
            }
        }
    }

    public async Task<bool> StartBackendAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_children.Count > 0)
            {
                return IsRunning;
            }

            LastError = null;
        }

        var startedChildren = new List<IBackendProcessHandle>();
        IBackendJob? job = null;
        BackendLaunchPlan? plan = null;
        try
        {
            plan = BackendLaunchPlan.Create(
                projectRoot,
                AuthSecret,
                WorkerSecret,
                WorkerPort,
                edgeExecutable: Environment.GetEnvironmentVariable("RU_RUBACKEND_EXE"),
                brokerPort: BrokerPort == 0 ? null : BrokerPort,
                brokerExecutable: Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE"));
            job = _jobFactory.Create();
            foreach (var spec in plan.Children)
            {
                var child = _processFactory.Start(spec);
                startedChildren.Add(child);
                job.Assign(child);
            }

            var ready = await _readinessProbe.WaitUntilReadyAsync(
                Port,
                AuthSecret,
                cancellationToken);
            if (!ready)
            {
                throw new InvalidOperationException(
                    $"Backend did not become ready on 127.0.0.1:{Port}.");
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    Cleanup(startedChildren, job, plan);
                    return false;
                }

                _children = startedChildren;
                _job = job;
                _brokerDescriptorPath = plan.BrokerDescriptorPath;
                _brokerSessionRoot = plan.BrokerSessionRoot;
                BrokerPort = plan.BrokerPort;
                BrokerDescriptorPath = plan.BrokerDescriptorPath;
                BrokerSessionRoot = plan.BrokerSessionRoot;
                job = null;
            }
            _ = WatchForExitAsync(startedChildren);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastError = "Backend startup was canceled.";
            Cleanup(startedChildren, job, plan);
            return false;
        }
        catch (Exception error)
        {
            LastError = error is FileNotFoundException
                ? error.Message
                : $"Could not start {DisplayName} backend: {error.Message}";
            Debug.WriteLine(
                $"[BackendProcessManager] Startup failed: {error.GetType().Name}: {error.Message}");
            Cleanup(startedChildren, job, plan);
            return false;
        }
    }

    private async Task WatchForExitAsync(IReadOnlyList<IBackendProcessHandle> children)
    {
        try
        {
            var exitTasks = children
                .Where(child => !child.HasExited)
                .Select(child => Task.Run(() => child.WaitForExit(Timeout.InfiniteTimeSpan)))
                .ToList();

            if (exitTasks.Count > 0)
            {
                await Task.WhenAny(exitTasks);
            }

            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_children, children))
                {
                    return;
                }
            }

            BackendExited?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            Debug.WriteLine(
                $"[BackendProcessManager] Exit watch failed: {error.GetType().Name}: {error.Message}");
        }
    }

    public void Dispose()
    {
        List<IBackendProcessHandle> children;
        IBackendJob? job;
        string? descriptorPath;
        string? sessionRoot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            children = _children;
            _children = new List<IBackendProcessHandle>();
            job = _job;
            _job = null;
            descriptorPath = _brokerDescriptorPath;
            sessionRoot = _brokerSessionRoot;
            _brokerDescriptorPath = null;
            _brokerSessionRoot = null;
        }

        Cleanup(children, job, descriptorPath, sessionRoot);
        BrokerDescriptorPath = null;
        BrokerSessionRoot = null;
        _httpClient.Dispose();
    }

    private static int ParseWorkerPort()
    {
        var raw = Environment.GetEnvironmentVariable("RU_BACKEND_WORKER_PORT");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 8005;
        }

        if (!int.TryParse(raw, out var workerPort))
        {
            throw new ArgumentException("RU_BACKEND_WORKER_PORT must be an integer.", nameof(raw));
        }

        if (workerPort is < 1024 or > 65535 || workerPort == 8000 || workerPort == 8001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(raw),
                "RU_BACKEND_WORKER_PORT must be between 1024 and 65535 and cannot be 8000 (public host) or 8001 (VTube Studio).");
        }

        return workerPort;
    }

    private static int ParseBrokerPort()
    {
        var raw = Environment.GetEnvironmentVariable("RU_BACKEND_BROKER_PORT");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (!int.TryParse(raw, out var brokerPort))
        {
            throw new ArgumentException("RU_BACKEND_BROKER_PORT must be an integer.", nameof(raw));
        }

        if (brokerPort is < 1024 or > 65535 || brokerPort == 8000 || brokerPort == 8001)
        {
            throw new ArgumentOutOfRangeException(
                nameof(raw),
                "RU_BACKEND_BROKER_PORT must be 0 or between 1024 and 65535 and cannot be 8000 (public host) or 8001 (VTube Studio).");
        }

        return brokerPort;
    }

    private static void Cleanup(
        IEnumerable<IBackendProcessHandle> children,
        IBackendJob? job,
        BackendLaunchPlan? plan = null)
    {
        Cleanup(children, job, plan?.BrokerDescriptorPath, plan?.BrokerSessionRoot);
    }

    private static void Cleanup(
        IEnumerable<IBackendProcessHandle> children,
        IBackendJob? job,
        string? descriptorPath,
        string? sessionRoot)
    {
        var childList = children.Reverse().ToList();
        foreach (var child in childList)
        {
            try
            {
                child.Kill();
            }
            catch (Exception error)
            {
                Debug.WriteLine($"[BackendProcessManager] Child kill failed: {error.GetType().Name}");
            }

            try
            {
                child.WaitForExit(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception error)
            {
                Debug.WriteLine($"[BackendProcessManager] Child wait failed: {error.GetType().Name}");
            }

            try
            {
                child.Dispose();
            }
            catch (Exception error)
            {
                Debug.WriteLine($"[BackendProcessManager] Child dispose failed: {error.GetType().Name}");
            }
        }

        try
        {
            job?.Dispose();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[BackendProcessManager] Job dispose failed: {error.GetType().Name}");
        }

        DeleteOwnedArtifacts(descriptorPath, sessionRoot);
    }

    private static void DeleteOwnedArtifacts(string? descriptorPath, string? sessionRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(descriptorPath) && File.Exists(descriptorPath))
            {
                File.Delete(descriptorPath);
            }

            if (!string.IsNullOrWhiteSpace(sessionRoot) && Directory.Exists(sessionRoot))
            {
                Directory.Delete(sessionRoot, recursive: true);
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[BackendProcessManager] Broker artifact cleanup failed: {error.GetType().Name}");
        }
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
