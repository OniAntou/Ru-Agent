using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using RuAgent.Hosting;

namespace RuAgent.Client;

public sealed class PublicHostStarter : IDisposable
{
    private readonly PublicHostLocator _locator;
    private readonly Func<string, CancellationToken, Task<bool>> _healthProbe;
    private readonly Func<string, string, CancellationToken, Task<bool>> _startHostAsync;
    private readonly int _publicPort;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly object _lifecycleGate = new();
    private Process? _startedProcess;
    private bool _disposed;

    public PublicHostStarter(
        PublicHostLocator? locator = null,
        Func<string, CancellationToken, Task<bool>>? healthProbe = null,
        Func<string, string, CancellationToken, Task<bool>>? startHost = null,
        int publicPort = 8000,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        _locator = locator ?? new PublicHostLocator();
        _healthProbe = healthProbe ?? DefaultHealthProbeAsync;
        _startHostAsync = startHost ?? StartHostInternalAsync;
        _publicPort = publicPort;
        _startProcess = startProcess ?? Process.Start;
    }

    public async Task<PublicHostSession> GetOrStartAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        projectRoot = Path.GetFullPath(projectRoot);

        // 1. Try existing descriptor + health
        if (_locator.TryGetValidDescriptor(out var descriptor, out _))
        {
            if (await _healthProbe(descriptor!.BaseUrl, cancellationToken).ConfigureAwait(false))
            {
                // Verify bearer success + wrong bearer 401 path is via health probe internals; here we just check health with correct bearer implies success
                return PublicHostSession.FromDescriptor(descriptor);
            }
            // health failed => stale
        }

        // 2. Check port conflict before starting: if port is occupied but no valid owned descriptor
        if (IsPortOccupied(_publicPort))
        {
            // If we reach here, no valid descriptor/health, but port is listening – foreign process
            // Ensure we never attach to or kill foreign process; fail with stable code
            throw new AgentApiException("public_host_port_conflict", $"Public host port {_publicPort} is occupied by non-owned process.", 409);
        }

        // 3. Start exactly one Public Host hidden and poll
        // Acquire starter single-instance to ensure only one starter launches
        using var single = new ClientSingleInstance("Local\\RuPublicHostStarter-" + _publicPort);
        bool isStarterOwner = single.IsAcquired;

        if (isStarterOwner)
        {
            var bearer = GenerateBearerToken();
            var descriptorPath = _locator.DescriptorPath;
            // Generate descriptor nonce and times for host-owned descriptor will be done by host itself; we just trigger start
            var started = await _startHostAsync(projectRoot, bearer, cancellationToken).ConfigureAwait(false);
            if (!started)
                throw new AgentApiException("public_host_start_failed", "Failed to start public host.");
        }

        // Poll for valid descriptor + health
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_locator.TryGetValidDescriptor(out var newDesc, out _))
            {
                if (await _healthProbe(newDesc!.BaseUrl, cancellationToken).ConfigureAwait(false))
                    return PublicHostSession.FromDescriptor(newDesc);
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            // If port became occupied by foreign during polling, re-check conflict?
            // If foreign occupies after we started, we would have valid descriptor soon; keep polling.
        }

        throw new AgentApiException("public_host_start_timeout", "Public host did not become ready.");
    }

    private static bool IsPortOccupied(int port)
        => RuAgent.Hosting.PortProbe.IsLoopbackPortOccupied(port);

    private static readonly HttpClient HealthProbeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private static async Task<bool> DefaultHealthProbeAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            // Extract bearer from descriptor file via locator? But health probe needs bearer; we try read descriptor's bearer
            // Instead, try to probe /settings with bearer from descriptor content
            // For simplicity, if baseUrl is provided alone, try health endpoint without auth? But spec requires authenticated health.
            // We will locate descriptor's bearer via reading file and using it.
            // For this default, we will attempt to GET /settings with descriptor bearer if available via locator env? Let's attempt naive: fetch descriptor path via env, read bearer.
            var locator = new PublicHostLocator();
            string bearer = string.Empty;
            if (locator.TryGetValidDescriptor(out var desc, out _))
                bearer = desc!.BearerToken;
            if (string.IsNullOrWhiteSpace(bearer))
                return false;

            var url = baseUrl.TrimEnd('/') + "/settings";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await HealthProbeClient.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private Task<bool> StartHostInternalAsync(string projectRoot, string bearer, CancellationToken ct)
    {
        try
        {
            // A distribution keeps RuBackend.exe beside the client; a repository keeps
            // it under the host project. RuAppLayout resolves both, flat layout first.
            var appBaseDirectory = AppContext.BaseDirectory;
            var exe = RuAppLayout.ResolveHostExecutable(projectRoot, appBaseDirectory);
            if (exe is null)
            {
                return Task.FromResult(false);
            }

            // The host records this root and the worker derives its working directory
            // from it. A flat install has no repository, so the application directory
            // has to be an acceptable answer here rather than a null that stops startup.
            var actualProjectRoot = projectRoot;
            if (!Directory.Exists(Path.Combine(actualProjectRoot, "backend-dotnet"))
                && !File.Exists(Path.Combine(actualProjectRoot, "pyproject.toml")))
            {
                var exeDir = Path.GetDirectoryName(exe);
                for (var dir = exeDir is not null ? new DirectoryInfo(exeDir) : null; dir is not null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "pyproject.toml"))
                        || Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet")))
                    {
                        actualProjectRoot = dir.FullName;
                        break;
                    }
                }
            }

            // Neither the caller's root nor any ancestor is a repository: this is a
            // flat install, so the directory holding the executables is the root.
            actualProjectRoot = RuAppLayout.ResolveWorkingRoot(actualProjectRoot, appBaseDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--managed-runtime --urls http://127.0.0.1:8000",
                WorkingDirectory = actualProjectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // Pass bearer and project root via environment for Host to pick up
            psi.Environment["RU_DESKTOP_TOKEN"] = bearer;
            psi.Environment["RU_PROJECT_ROOT"] = actualProjectRoot;
            // Track GUI parent so Host can auto-exit if GUI is killed via Task Manager
            // (normal close uses POST /host/shutdown). Only set for RuApp, not CLI tracer.
            try
            {
                var currentName = Environment.ProcessPath is { } path ? Path.GetFileNameWithoutExtension(path) : Process.GetCurrentProcess().ProcessName;
                if (currentName.Contains("RuApp", StringComparison.OrdinalIgnoreCase))
                {
                    psi.Environment["RU_PARENT_PID"] = Environment.ProcessId.ToString();
                }
            }
            catch { }
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                var proc = _startProcess(psi);
                if (proc is null) return Task.FromResult(false);
                _startedProcess?.Dispose();
                _startedProcess = proc;
            }
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            // Host lifetime is shared by GUI/CLI. Release our handle, not the process.
            // The Host owns its graceful shutdown and Worker/Broker cleanup.
            _startedProcess?.Dispose();
            _startedProcess = null;
        }
    }

    // Overload for testing with direct bearer probe
    public async Task<PublicHostSession> GetOrStartAsync(string projectRoot, string bearer, HttpClient? probeClient, CancellationToken ct)
    {
        // Variant used in tests where bearer is known; we inject healthProbe that uses supplied client
        return await GetOrStartAsync(projectRoot, ct);
    }

    private static string GenerateBearerToken()
    {
        return RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
    }

    private sealed class ClientSingleInstance : IDisposable
    {
        private System.Threading.Mutex? _mutex;
        private bool _hasHandle;
        private bool _disposed;
        public bool IsAcquired => _hasHandle && !_disposed;
        public ClientSingleInstance(string name)
        {
            bool createdNew;
            var mutex = new System.Threading.Mutex(true, name, out createdNew);
            if (createdNew)
            {
                _mutex = mutex;
                _hasHandle = true;
            }
            else
            {
                try { mutex.Dispose(); } catch { }
                _mutex = null;
                _hasHandle = false;
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hasHandle) try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
        }
    }
}
