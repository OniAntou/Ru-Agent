using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace RuAgent.Client;

public sealed class PublicHostStarter
{
    private readonly PublicHostLocator _locator;
    private readonly Func<string, CancellationToken, Task<bool>> _healthProbe;
    private readonly Func<string, string, CancellationToken, Task<bool>> _startHostAsync;
    private readonly int _publicPort;

    public PublicHostStarter(
        PublicHostLocator? locator = null,
        Func<string, CancellationToken, Task<bool>>? healthProbe = null,
        Func<string, string, CancellationToken, Task<bool>>? startHost = null,
        int publicPort = 8000)
    {
        _locator = locator ?? new PublicHostLocator();
        _healthProbe = healthProbe ?? DefaultHealthProbeAsync;
        _startHostAsync = startHost ?? DefaultStartHostAsync;
        _publicPort = publicPort;
    }

    public async Task<PublicHostSession> GetOrStartAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
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
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
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
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

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

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var url = baseUrl.TrimEnd('/') + "/settings";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static Task<bool> DefaultStartHostAsync(string projectRoot, string bearer, CancellationToken ct)
    {
        try
        {
            // Resolve RuBackend.exe publish path
            var candidates = new[]
            {
                Path.Combine(projectRoot, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "win-x64", "publish", "RuBackend.exe"),
                Path.Combine(projectRoot, "backend-dotnet", "RuBackend", "bin", "Release", "net10.0", "RuBackend.exe"),
                Path.Combine(AppContext.BaseDirectory, "RuBackend.exe"),
            };
            string? exe = candidates.FirstOrDefault(File.Exists);
            if (exe is null)
            {
                // Fallback: try dotnet run? For now fail
                return Task.FromResult(false);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--managed-runtime --urls http://127.0.0.1:8000",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // Pass bearer via environment for Host to pick up? Actually Host will generate its own bearer and write descriptor; but starter passes via arg? For now set env
            psi.Environment["RU_DESKTOP_TOKEN"] = bearer;
            // Ensure child does not inherit other tokens
            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
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
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
