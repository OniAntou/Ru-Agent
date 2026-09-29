using System.Net.Http;
using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class RuAppComposition : IDisposable
{
    private readonly AgentAuditLog _auditLog = new();

    // Long-lived clients: one per composition, shared by every run it drives.
    // Creating an HttpClient per run/stream wastes sockets on TIME_WAIT reuse,
    // and mutating a shared client's timeout is not an option, so the API and
    // SSE transports get separate instances.
    private HttpClient? _agentApiHttpClient;
    private HttpClient? _agentSseHttpClient;

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
        _agentApiHttpClient?.Dispose();
        _agentSseHttpClient?.Dispose();
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

        _agentApiHttpClient ??= new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        _agentSseHttpClient ??= new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // An SSE connection is long-lived by design; the caller token bounds
            // it. Sharing the API client would impose its request timeout instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var hostSession = ToPublicHostSession(session);
        var client = new AgentRunClientAdapter(
            hostSession,
            _agentApiHttpClient,
            sseHttpClient: _agentSseHttpClient);
        Action<Action>? synchronize = dispatcher is null ? null : action => DispatchToUi(dispatcher, action);
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
        Action<Action>? synchronize = dispatcher is null ? null : action => DispatchToUi(dispatcher, action);
        return new AgentPolicyController(client, synchronize);
    }

    /// <summary>
    /// Marshals an action onto the UI thread. On the UI thread it runs inline;
    /// when the dispatcher has closed (shutdown), it is dropped and traced —
    /// running a UI-bound assignment on the calling pool thread is corruption,
    /// not a fallback.
    /// </summary>
    private static void DispatchToUi(DispatcherQueue dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        if (!dispatcher.TryEnqueue(() => action()))
        {
            GuiDiagnosticWriter.AppendTraceLine(
                "[RuAppComposition] dispatcher rejected a UI transition during shutdown; update dropped.");
        }
    }
}
