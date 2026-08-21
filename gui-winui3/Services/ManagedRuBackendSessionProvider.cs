namespace RuApp.Services;

public sealed class ManagedRuBackendSessionProvider : IRuBackendSessionProvider
{
    private readonly BackendProcessManager _manager;
    private bool _disposed;

    public ManagedRuBackendSessionProvider(BackendProcessManager? manager = null)
    {
        _manager = manager ?? new BackendProcessManager();
        _manager.BackendExited += OnBackendExited;
    }

    public string DisplayName => _manager.DisplayName;
    public BackendSession? Session { get; private set; }
    public event EventHandler? BackendExited;

    public async Task<BackendSession?> StartAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _manager.StartBackendAsync(projectRoot, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Session = BackendSession.FromLoopback(_manager.Port, _manager.AuthSecret);
        return Session;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.BackendExited -= OnBackendExited;
        _manager.Dispose();
    }

    private void OnBackendExited(object? sender, EventArgs args) => BackendExited?.Invoke(this, args);
}
