using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class RuAppComposition : IDisposable
{
    private readonly AgentAuditLog _auditLog = new();

    private RuAppComposition(
        IRuBackendSessionProvider backendProvider,
        bool enableSubtitleOverlay,
        string? harnessEventsPath,
        string? harnessWorkspacePath,
        Func<IVoiceFrameSource>? voiceSourceFactory,
        Func<IVoicePlaybackDevice>? playbackDeviceFactory,
        HarnessEventSink? harnessEvents,
        string? harnessScenario,
        bool isHarness = false)
    {
        BackendProvider = backendProvider;
        EnableSubtitleOverlay = enableSubtitleOverlay;
        HarnessEventsPath = harnessEventsPath;
        HarnessWorkspacePath = harnessWorkspacePath;
        VoiceSourceFactory = voiceSourceFactory;
        PlaybackDeviceFactory = playbackDeviceFactory;
        HarnessEvents = harnessEvents;
        HarnessScenario = harnessScenario;
        IsHarness = isHarness;
    }

    public IRuBackendSessionProvider BackendProvider { get; }
    public bool EnableSubtitleOverlay { get; }
    public string? HarnessEventsPath { get; }
    public string? HarnessWorkspacePath { get; }
    public Func<IVoiceFrameSource>? VoiceSourceFactory { get; }
    public Func<IVoicePlaybackDevice>? PlaybackDeviceFactory { get; }
    public HarnessEventSink? HarnessEvents { get; }
    public string? HarnessScenario { get; }
    public bool IsHarness { get; }
    public AgentAuditLog AuditLog => _auditLog;

    public static RuAppComposition Production() => new(
        new ManagedRuBackendSessionProvider(),
        enableSubtitleOverlay: true,
        harnessEventsPath: null,
        harnessWorkspacePath: null,
        voiceSourceFactory: null,
        playbackDeviceFactory: null,
        harnessEvents: null,
        harnessScenario: null,
        isHarness: false);

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
            descriptor.Scenario,
            descriptor.WorkspacePath);
    }

    public static RuAppComposition ForHarness(
        IRuBackendSessionProvider backendProvider,
        string eventsPath,
        Func<IVoiceFrameSource>? voiceSourceFactory = null,
        Func<IVoicePlaybackDevice>? playbackDeviceFactory = null,
        HarnessEventSink? harnessEvents = null,
        string? harnessScenario = null,
        string? harnessWorkspacePath = null)
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
            harnessWorkspacePath,
            voiceSourceFactory ?? (() => new DeterministicVoiceFrameSource()),
            playbackDeviceFactory ?? (() => new RecordingVoicePlaybackDevice(events)),
            events,
            harnessScenario ?? "turn",
            isHarness: true);
    }

    public void Dispose()
    {
        HarnessEvents?.Dispose();
        BackendProvider.Dispose();
        _auditLog.Dispose();
    }

    public static PublicHostSession ToPublicHostSession(BackendSession session)
    {
        var descriptor = new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            session.HttpBaseUri.ToString(),
            session.AuthSecret,
            Environment.ProcessId,
            DateTime.UtcNow.ToString("O"),
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    public AgentRunController? CreateAgentRunController(DispatcherQueue? dispatcher = null)
    {
        var session = BackendProvider.Session;
        if (session is null)
        {
            return null;
        }

        var client = new AgentRunClientAdapter(ToPublicHostSession(session));
        Action<Action>? synchronize = dispatcher is null ? null : action =>
        {
            if (!dispatcher.TryEnqueue(() => action()))
            {
                // During shutdown the dispatcher may reject work. Preserve the
                // state transition instead of dropping it silently.
                action();
            }
        };
        return new AgentRunController(client, HarnessEvents, synchronize, _auditLog.Record);
    }

    public AgentPolicyController? CreateAgentPolicyController(DispatcherQueue? dispatcher = null)
    {
        var session = BackendProvider.Session;
        if (session is null)
        {
            return null;
        }

        var client = new AgentApiClient(ToPublicHostSession(session));
        Action<Action>? synchronize = dispatcher is null ? null : action =>
        {
            if (!dispatcher.TryEnqueue(() => action()))
            {
                action();
            }
        };
        return new AgentPolicyController(client, synchronize);
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
