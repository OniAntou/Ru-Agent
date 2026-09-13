using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class RuAppComposition : IDisposable
{
    private readonly AgentAuditLog _auditLog = new();

    private RuAppComposition(
        IRuBackendSessionProvider backendProvider,
        string? harnessEventsPath,
        string? harnessWorkspacePath,
        HarnessEventSink? harnessEvents,
        string? harnessScenario,
        bool isHarness = false)
    {
        BackendProvider = backendProvider;
        HarnessEventsPath = harnessEventsPath;
        HarnessWorkspacePath = harnessWorkspacePath;
        HarnessEvents = harnessEvents;
        HarnessScenario = harnessScenario;
        IsHarness = isHarness;
    }

    public IRuBackendSessionProvider BackendProvider { get; }
    public string? HarnessEventsPath { get; }
    public string? HarnessWorkspacePath { get; }
    public HarnessEventSink? HarnessEvents { get; }
    public string? HarnessScenario { get; }
    public bool IsHarness { get; }
    public AgentAuditLog AuditLog => _auditLog;

    public static RuAppComposition Production() => new(
        new ManagedRuBackendSessionProvider(),
        harnessEventsPath: null,
        harnessWorkspacePath: null,
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

        return ForHarness(
            provider,
            descriptor.EventsPath,
            events,
            descriptor.Scenario,
            descriptor.WorkspacePath);
    }

    public static RuAppComposition ForHarness(
        IRuBackendSessionProvider backendProvider,
        string eventsPath,
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
            Path.GetFullPath(eventsPath),
            harnessWorkspacePath,
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
}
