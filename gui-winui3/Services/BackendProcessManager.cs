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
    private bool _disposed;

    public BackendProcessManager(
        BackendHostMode? mode = null,
        int? workerPort = null,
        IBackendProcessFactory? processFactory = null,
        IBackendJobFactory? jobFactory = null,
        IBackendReadinessProbe? readinessProbe = null)
    {
        Mode = mode ?? BackendHostModeParser.Parse(
            Environment.GetEnvironmentVariable("RU_BACKEND_HOST_MODE"));
        Port = 8000;
        WorkerPort = Mode == BackendHostMode.AspNetCore
            ? workerPort ?? ParseWorkerPort()
            : null;
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
    public int? WorkerPort { get; }
    public BackendHostMode Mode { get; }
    public string DisplayName => Mode == BackendHostMode.AspNetCore
        ? "ASP.NET Core edge"
        : "FastAPI";
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
        try
        {
            var plan = BackendLaunchPlan.Create(
                projectRoot,
                AuthSecret,
                WorkerSecret,
                Mode,
                WorkerPort ?? Port,
                Environment.GetEnvironmentVariable("RU_RUBACKEND_EXE"));
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
                _children = startedChildren;
                _job = job;
                job = null;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastError = "Backend startup was canceled.";
            Cleanup(startedChildren, job);
            return false;
        }
        catch (Exception error)
        {
            LastError = error is FileNotFoundException
                ? error.Message
                : $"Could not start {DisplayName} backend: {error.Message}";
            Debug.WriteLine(
                $"[BackendProcessManager] Startup failed: {error.GetType().Name}: {error.Message}");
            Cleanup(startedChildren, job);
            return false;
        }
    }

    public void Dispose()
    {
        List<IBackendProcessHandle> children;
        IBackendJob? job;
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
        }

        Cleanup(children, job);
        _httpClient.Dispose();
    }

    private static int ParseWorkerPort()
    {
        var raw = Environment.GetEnvironmentVariable("RU_BACKEND_WORKER_PORT");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 8001;
        }

        if (!int.TryParse(raw, out var workerPort))
        {
            throw new ArgumentException("RU_BACKEND_WORKER_PORT must be an integer.", nameof(raw));
        }

        if (workerPort is < 1024 or > 65535 || workerPort == 8000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(raw),
                "RU_BACKEND_WORKER_PORT must be between 1024 and 65535 and cannot be 8000.");
        }

        return workerPort;
    }

    private static void Cleanup(
        IEnumerable<IBackendProcessHandle> children,
        IBackendJob? job)
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
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
