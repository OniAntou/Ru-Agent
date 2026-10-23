using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>How long a killed host process gets to exit before its handle is released.</summary>
    private const int KillWaitTimeoutMilliseconds = 5000;

    // Startup exit codes of the managed host (ManagedRuntimeBootstrapper in the
    // RuBackend process). The child is a separate process, so its exit code is
    // the only channel it has to report why it never became ready.
    private const int HostExitPortConflict = 2;
    private const int HostExitAlreadyRunning = 3;

    public PublicHostStarter(
        PublicHostLocator? locator = null,
        Func<string, CancellationToken, Task<bool>>? healthProbe = null,
        Func<string, string, CancellationToken, Task<bool>>? startHost = null,
        int publicPort = PublicHostRuntimeOptions.DefaultPublicPort,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        if (publicPort != 0 && (publicPort < 1024 || publicPort > 65535))
            throw new ArgumentOutOfRangeException(nameof(publicPort));
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

        // Start exactly one Public Host hidden and poll.
        // Acquire starter single-instance to ensure only one starter launches
        await using var single = await RuAgent.Protocol.ThreadOwnedMutex.AcquireAsync(CreateStarterMutexName(_locator.DescriptorPath))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool isStarterOwner = single.IsAcquired;

        if (_locator.TryGetValidDescriptor(out var currentDescriptor, out _)
            && await _healthProbe(currentDescriptor!.BaseUrl, cancellationToken).ConfigureAwait(false))
        {
            return PublicHostSession.FromDescriptor(currentDescriptor);
        }

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

            // The child's exit code is the authoritative bind outcome: a foreign
            // process that bound after the pre-check makes the host exit before
            // it ever writes a descriptor, and polling to the deadline would
            // report a misleading start_timeout instead of the real conflict.
            if (isStarterOwner && ReadOwnedHostExitCode() is { } exitCode)
            {
                StopOwnedHost();
                throw MapHostStartupExit(exitCode);
            }

            if (_locator.TryGetValidDescriptor(out var newDesc, out _))
            {
                if (await _healthProbe(newDesc!.BaseUrl, cancellationToken).ConfigureAwait(false))
                    return PublicHostSession.FromDescriptor(newDesc);
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        // We started this host and it never became ready. Reap exactly the tree
        // we spawned -- never a host another client owns (for them
        // _startedProcess is null and this is a no-op).
        if (isStarterOwner)
        {
            StopOwnedHost();
        }

        throw new AgentApiException("public_host_start_timeout", "Public host did not become ready.");
    }

    private void StopOwnedHost()
    {
        lock (_lifecycleGate)
        {
            if (_startedProcess is null)
            {
                return;
            }

            try
            {
                if (!_startedProcess.HasExited)
                {
                    _startedProcess.Kill(entireProcessTree: true);
                    _startedProcess.WaitForExit(KillWaitTimeoutMilliseconds);
                }
            }
            catch (Exception)
            {
                // Best-effort reap: the process may have exited concurrently or
                // be owned by a hardened parent. The handle is released below
                // either way.
            }

            _startedProcess.Dispose();
            _startedProcess = null;
        }
    }

    private int? ReadOwnedHostExitCode()
    {
        lock (_lifecycleGate)
        {
            if (_startedProcess is not { } process)
            {
                return null;
            }

            try
            {
                return process.HasExited ? process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                // The handle was already released, so there is nothing left to observe.
                return null;
            }
        }
    }

    private AgentApiException MapHostStartupExit(int exitCode) => exitCode switch
    {
        HostExitPortConflict => new AgentApiException(
            "public_host_port_conflict",
            "The configured Public Host port is occupied by another process.",
            409),
        HostExitAlreadyRunning => new AgentApiException(
            "public_host_already_running",
            "Another public host claimed this runtime before the started one could bind.",
            409),
        _ => new AgentApiException(
            "public_host_start_failed",
            $"Public host exited with code {exitCode} before it became ready."),
    };

    private static string CreateStarterMutexName(string descriptorPath)
    {
        var profileRoot = Path.GetDirectoryName(Path.GetFullPath(descriptorPath))
            ?? throw new ArgumentException("Runtime descriptor path has no profile root.", nameof(descriptorPath));
        profileRoot = Path.TrimEndingDirectorySeparator(profileRoot).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(profileRoot));
        return "Local\\RuPublicHostStarter-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static readonly HttpClient HealthProbeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private async Task<bool> DefaultHealthProbeAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            // The bearer has to come from the same descriptor store this starter
            // reads; constructing a fresh locator here silently ignored the
            // instance one and could probe a host the caller never pointed at.
            string bearer = string.Empty;
            if (_locator.TryGetValidDescriptor(out var desc, out _))
                bearer = desc!.BearerToken;
            if (string.IsNullOrWhiteSpace(bearer))
                return false;

            var url = baseUrl.TrimEnd('/') + "/settings";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await HealthProbeClient.SendAsync(req, ct).ConfigureAwait(false);
            var installed = RuAgent.Updates.InstalledRelease.Load(AppContext.BaseDirectory);
            if (resp.IsSuccessStatusCode && installed is not null)
            {
                using var versionRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/status");
                versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var versionResponse = await HealthProbeClient.SendAsync(versionRequest, ct).ConfigureAwait(false);
                using var version = System.Text.Json.JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!versionResponse.IsSuccessStatusCode
                    || !version.RootElement.TryGetProperty("software_version", out var reported)
                    || reported.ValueKind != System.Text.Json.JsonValueKind.String
                    || reported.GetString() != installed.Version)
                    throw new AgentApiException("host_version_mismatch", "Close the other Ru application before starting this version.");
            }
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentApiException) { throw; }
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

            // The host records this root and derives its working directory from it.
            // A flat install has no repository, so the application directory has to
            // be an acceptable answer here rather than a null that stops startup.
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
                // --urls has to come before the value-less managed-runtime switch
                // because the command-line configuration provider treats the
                // switch as consuming the next token. Port zero delegates port
                // selection to Kestrel.
                Arguments = $"--urls http://127.0.0.1:{_publicPort} --managed-runtime",
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            // The Host owns its graceful shutdown and Broker cleanup.
            _startedProcess?.Dispose();
            _startedProcess = null;
        }
    }

    private static string GenerateBearerToken()
    {
        return RuAgent.Hosting.PublicHostRuntimeOptions.GenerateBearerToken();
    }

}
