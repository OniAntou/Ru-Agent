namespace RuApp.Services;

public sealed class RuAppComposition : IDisposable
{
    private RuAppComposition(
        IRuBackendSessionProvider backendProvider,
        bool enableSubtitleOverlay,
        string? harnessEventsPath,
        Func<IVoiceFrameSource>? voiceSourceFactory,
        Func<IVoicePlaybackDevice>? playbackDeviceFactory,
        HarnessEventSink? harnessEvents,
        string? harnessScenario)
    {
        BackendProvider = backendProvider;
        EnableSubtitleOverlay = enableSubtitleOverlay;
        HarnessEventsPath = harnessEventsPath;
        VoiceSourceFactory = voiceSourceFactory;
        PlaybackDeviceFactory = playbackDeviceFactory;
        HarnessEvents = harnessEvents;
        HarnessScenario = harnessScenario;
    }

    public IRuBackendSessionProvider BackendProvider { get; }
    public bool EnableSubtitleOverlay { get; }
    public string? HarnessEventsPath { get; }
    public Func<IVoiceFrameSource>? VoiceSourceFactory { get; }
    public Func<IVoicePlaybackDevice>? PlaybackDeviceFactory { get; }
    public HarnessEventSink? HarnessEvents { get; }
    public string? HarnessScenario { get; }
    public bool IsHarness => !EnableSubtitleOverlay;

    public static RuAppComposition Production() => new(
        new ManagedRuBackendSessionProvider(),
        enableSubtitleOverlay: true,
        harnessEventsPath: null,
        voiceSourceFactory: null,
        playbackDeviceFactory: null,
        harnessEvents: null,
        harnessScenario: null);

    public static RuAppComposition FromArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Production();
        }

        var descriptor = HarnessSessionDescriptor.LoadFromArgument(arguments);
        var session = new BackendSession(descriptor.BaseUri, descriptor.AuthToken);
        var provider = new ExternalRuBackendSessionProvider(session);
        var events = new HarnessEventSink(descriptor.EventsPath);
        var sourceOptions = CreateVoiceOptions(descriptor.Scenario);

        return ForHarness(
            provider,
            descriptor.EventsPath,
            () => new DeterministicVoiceFrameSource(sourceOptions),
            () => new RecordingVoicePlaybackDevice(events),
            events,
            descriptor.Scenario);
    }

    public static RuAppComposition ForHarness(
        IRuBackendSessionProvider backendProvider,
        string eventsPath,
        Func<IVoiceFrameSource>? voiceSourceFactory = null,
        Func<IVoicePlaybackDevice>? playbackDeviceFactory = null,
        HarnessEventSink? harnessEvents = null,
        string? harnessScenario = null)
    {
        ArgumentNullException.ThrowIfNull(backendProvider);
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            throw new ArgumentException("Harness event path is required.", nameof(eventsPath));
        }

        var events = harnessEvents ?? new HarnessEventSink(eventsPath);
        return new RuAppComposition(
            backendProvider,
            enableSubtitleOverlay: false,
            Path.GetFullPath(eventsPath),
            voiceSourceFactory ?? (() => new DeterministicVoiceFrameSource()),
            playbackDeviceFactory ?? (() => new RecordingVoicePlaybackDevice(events)),
            events,
            harnessScenario ?? "turn");
    }

    public void Dispose()
    {
        HarnessEvents?.Dispose();
        BackendProvider.Dispose();
    }

    private static DeterministicVoiceFrameOptions CreateVoiceOptions(string scenario)
    {
        return scenario.ToLowerInvariant() switch
        {
            "stop" or "stale-session" => new DeterministicVoiceFrameOptions(
                SpeechDelay: TimeSpan.FromMilliseconds(1000),
                EndDelay: TimeSpan.Zero),
            "cancel" => new DeterministicVoiceFrameOptions(
                SpeechDelay: TimeSpan.FromMilliseconds(100),
                EndDelay: TimeSpan.FromMilliseconds(100)),
            _ => new DeterministicVoiceFrameOptions(
                SpeechDelay: TimeSpan.FromMilliseconds(250),
                EndDelay: TimeSpan.Zero),
        };
    }
}
