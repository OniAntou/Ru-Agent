using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli.Rpc;

/// <summary>An application-level dispatch failure carrying the wire mapping.</summary>
public sealed class RpcDispatchException : Exception
{
    public RpcDispatchException(int rpcErrorCode, string message, string? ruCode = null, int? httpStatus = null)
        : base(message)
    {
        RpcErrorCode = rpcErrorCode;
        RuCode = ruCode;
        HttpStatus = httpStatus;
    }

    public int RpcErrorCode { get; }
    public string? RuCode { get; }
    public int? HttpStatus { get; }
}

/// <summary>The run-event subscription surface the dispatcher needs (implemented by RpcRunBridge).</summary>
public interface IRunBridge
{
    string Attach(string runId, long afterSequence, bool includeTransient);
    bool Detach(string subscriptionId);
}

public sealed class RpcDispatcher
{
    public static readonly IReadOnlyList<string> MethodNames =
    [
        "initialize",
        "host.status",
        "session.create",
        "session.list",
        "session.fork",
        "session.rewind",
        "run.start",
        "run.attach",
        "run.detach",
        "run.trust",
        "run.approve",
        "run.cancel",
        "run.continue",
        "run.status",
        "run.command",
        "run.reconcile",
        "run.output",
        "policy.list",
        "policy.import",
        "policy.remove",
    ];

    public static readonly IReadOnlyList<string> NotificationNames =
    [
        "run.event",
        "run.transient",
        "bridge.shutdown",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly PublicHostSession _session;
    private readonly SessionApiClient _sessionClient;
    private readonly AgentApiClient _agentClient;
    private readonly SettingsApiClient _settingsClient;
    private readonly RunStarter? _runStarter;
    private readonly IRunBridge? _runBridge;
    private readonly TextWriter _log;
    private readonly HttpClient _httpClient;
    private bool _initialized;

    public RpcDispatcher(
        PublicHostSession session,
        SessionApiClient sessionClient,
        AgentApiClient agentClient,
        SettingsApiClient settingsClient,
        RunStarter? runStarter,
        IRunBridge? runBridge,
        TextWriter log,
        HttpClient httpClient)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _settingsClient = settingsClient ?? throw new ArgumentNullException(nameof(settingsClient));
        _runStarter = runStarter;
        _runBridge = runBridge;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public bool IsInitialized => _initialized;

    public async Task<JsonNode?> DispatchAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "initialize")
        {
            return Initialize(request);
        }

        if (!_initialized)
        {
            throw new RpcDispatchException(RpcProtocol.NotInitialized, "initialize must be called first.");
        }

        try
        {
            return request.Method switch
            {
                "host.status" => await HostStatusAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new RpcDispatchException(RpcProtocol.MethodNotFound, $"method_not_found: {request.Method}"),
            };
        }
        catch (AgentApiException ex)
        {
            throw new RpcDispatchException(RpcProtocol.ApplicationError, ex.Message, ex.ErrorCode, ex.StatusCode);
        }
    }

    private JsonNode Initialize(RpcRequest request)
    {
        if (_initialized)
        {
            throw new RpcDispatchException(RpcProtocol.AlreadyInitialized, "initialize was already called.");
        }

        var protocolVersion = request.Params.TryGetProperty("protocolVersion", out var versionElement)
            && versionElement.ValueKind == JsonValueKind.Number
            && versionElement.TryGetInt32(out var version)
            ? version
            : -1;
        if (protocolVersion != RpcProtocol.Version)
        {
            throw new RpcDispatchException(
                RpcProtocol.ApplicationError,
                $"unsupported_protocol: this bridge speaks protocol {RpcProtocol.Version}.",
                ruCode: "unsupported_protocol");
        }

        _initialized = true;
        return new JsonObject
        {
            ["protocolVersion"] = RpcProtocol.Version,
            ["bridge"] = new JsonObject
            {
                ["version"] = typeof(RpcDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
            ["methods"] = new JsonArray(MethodNames.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["notifications"] = new JsonArray(NotificationNames.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
        };
    }

    private async Task<JsonNode> HostStatusAsync(CancellationToken cancellationToken)
    {
        await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["ready"] = true,
            ["base_url"] = _session.BaseUri.ToString(),
        };
    }
}
