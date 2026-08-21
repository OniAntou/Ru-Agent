namespace RuApp.Services;

public sealed class RuAppComposition : IDisposable
{
    private RuAppComposition(
        IRuBackendSessionProvider backendProvider,
        bool enableSubtitleOverlay,
        string? harnessEventsPath)
    {
        BackendProvider = backendProvider;
        EnableSubtitleOverlay = enableSubtitleOverlay;
        HarnessEventsPath = harnessEventsPath;
    }

    public IRuBackendSessionProvider BackendProvider { get; }
    public bool EnableSubtitleOverlay { get; }
    public string? HarnessEventsPath { get; }
    public bool IsHarness => !EnableSubtitleOverlay;

    public static RuAppComposition Production() => new(
        new ManagedRuBackendSessionProvider(),
        enableSubtitleOverlay: true,
        harnessEventsPath: null);

    public static RuAppComposition ForHarness(
        IRuBackendSessionProvider backendProvider,
        string eventsPath)
    {
        ArgumentNullException.ThrowIfNull(backendProvider);
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            throw new ArgumentException("Harness event path is required.", nameof(eventsPath));
        }

        return new RuAppComposition(backendProvider, enableSubtitleOverlay: false, eventsPath);
    }

    public void Dispose() => BackendProvider.Dispose();
}
