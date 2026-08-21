namespace RuApp.Services;

public sealed class ExternalRuBackendSessionProvider : IRuBackendSessionProvider
{
    private bool _disposed;

    public ExternalRuBackendSessionProvider(BackendSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public string DisplayName => "Harness edge";
    public BackendSession Session { get; }
    public event EventHandler? BackendExited
    {
        add { }
        remove { }
    }

    public Task<BackendSession?> StartAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<BackendSession?>(Session);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
