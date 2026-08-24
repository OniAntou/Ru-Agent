using RuAgent.Client;

namespace RuApp.Services;

public sealed class ManagedRuBackendSessionProvider : IRuBackendSessionProvider
{
    private readonly PublicHostStarter _starter;
    private readonly PublicHostLocator _locator;
    private bool _disposed;

    public ManagedRuBackendSessionProvider(PublicHostStarter? starter = null, PublicHostLocator? locator = null)
    {
        _locator = locator ?? new PublicHostLocator();
        _starter = starter ?? new PublicHostStarter(_locator);
    }

    public string DisplayName => "Public Host (managed)";
    public BackendSession? Session { get; private set; }
#pragma warning disable CS0067 // Interface requires event; managed host does not raise it – next StartAsync re-validates descriptor
    public event EventHandler? BackendExited;
#pragma warning restore CS0067

    public async Task<BackendSession?> StartAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Session is not null)
            return Session;

        try
        {
            var hostSession = await _starter.GetOrStartAsync(projectRoot, cancellationToken).ConfigureAwait(false);
            // Validate descriptor + health already done by starter; create BackendSession for Chat and future Agent
            var backendSession = BackendSession.FromLoopback(hostSession.BaseUri.Port, hostSession.BearerToken);
            Session = backendSession;
            return backendSession;
        }
        catch (AgentApiException ex) when (ex.ErrorCode == "public_host_port_conflict")
        {
            // Stable error for port conflict – do not attach to foreign process
            System.Diagnostics.Debug.WriteLine($"[ManagedRuBackendSessionProvider] Port conflict: {ex.Message}");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ManagedRuBackendSessionProvider] Start failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Do not kill Public Host – it owns Worker/Broker and outlives WinUI. Just release local state.
        // BackendExited is not raised for managed host; if host exits, next StartAsync will re-attach via descriptor validation.
    }
}
