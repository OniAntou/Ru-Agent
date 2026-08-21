namespace RuApp.Services;

public interface IRuBackendSessionProvider : IDisposable
{
    string DisplayName { get; }
    BackendSession? Session { get; }
    event EventHandler? BackendExited;
    Task<BackendSession?> StartAsync(string projectRoot, CancellationToken cancellationToken);
}
