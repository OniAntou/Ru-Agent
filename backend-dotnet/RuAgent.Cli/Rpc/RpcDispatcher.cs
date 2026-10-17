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

    /// <summary>
    /// Cancels every subscription for one run and returns how many were stopped.
    /// The bounded cleanup for a client whose attach response never arrived: the
    /// subscription may exist server-side without its id ever reaching the caller.
    /// </summary>
    int DetachRun(string runId);
}

public sealed class RpcDispatcher
{
    public static readonly IReadOnlyList<string> MethodNames =
    [
        "initialize",
        "host.status",
        "session.create",
        "session.list",
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
        "run.detached",
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
                "session.create" => await SessionCreateAsync(cancellationToken).ConfigureAwait(false),
                "session.list" => await SessionListAsync(cancellationToken).ConfigureAwait(false),
                "session.rewind" => await SessionRewindAsync(request, cancellationToken).ConfigureAwait(false),
                "run.start" => await RunStartAsync(request, cancellationToken).ConfigureAwait(false),
                "run.attach" => await RunAttachAsync(request, cancellationToken).ConfigureAwait(false),
                "run.detach" => RunDetach(request),
                "run.trust" => await RunTrustAsync(request, cancellationToken).ConfigureAwait(false),
                "run.approve" => await RunApproveAsync(request, cancellationToken).ConfigureAwait(false),
                "run.cancel" => await RunCancelAsync(request, cancellationToken).ConfigureAwait(false),
                "run.continue" => await RunContinueAsync(request, cancellationToken).ConfigureAwait(false),
                "run.status" => await RunStatusAsync(request, cancellationToken).ConfigureAwait(false),
                "run.command" => await RunCommandAsync(request, cancellationToken).ConfigureAwait(false),
                "run.reconcile" => await RunReconcileAsync(request, cancellationToken).ConfigureAwait(false),
                "run.output" => await RunOutputAsync(request, cancellationToken).ConfigureAwait(false),
                "policy.list" => await PolicyListAsync(request, cancellationToken).ConfigureAwait(false),
                "policy.import" => await PolicyImportAsync(request, cancellationToken).ConfigureAwait(false),
                "policy.remove" => await PolicyRemoveAsync(request, cancellationToken).ConfigureAwait(false),
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

    private async Task<JsonNode> SessionCreateAsync(CancellationToken ct)
    {
        var sessionId = await _sessionClient.CreateAsync(ct).ConfigureAwait(false);
        return new JsonObject { ["session_id"] = sessionId };
    }

    private async Task<JsonNode> SessionListAsync(CancellationToken ct)
    {
        var result = await _sessionClient.ListAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(result.GetRawText())!;
    }

    private async Task<JsonNode> SessionRewindAsync(RpcRequest request, CancellationToken ct)
    {
        var sessionId = RequireString(request, "sessionId", 64);
        var index = RequireInt(request, "index");
        var result = await _sessionClient.RewindAsync(sessionId, index, ct).ConfigureAwait(false);
        return JsonNode.Parse(result.GetRawText())!;
    }

    private async Task<JsonNode> RunStartAsync(RpcRequest request, CancellationToken ct)
    {
        var workspace = RequireString(request, "workspace", AgentCliParser.MaxWorkspacePathLength);
        var prompt = RequireString(request, "prompt", AgentCliParser.MaxPromptLength);
        var preset = OptionalString(request, "preset", 16) ?? "standard";
        if (!RuAgent.Protocol.Harness.AgentPolicyProfileCatalog.IsKnownPreset(preset))
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_preset: preset must be plan, standard, or full_access.", "invalid_preset");
        }

        var confirmFullAccess = OptionalBool(request, "confirmFullAccess") ?? false;
        if (preset == "full_access" && !confirmFullAccess)
        {
            throw new RpcDispatchException(
                RpcProtocol.ApplicationError,
                "full_access_confirmation_required: --confirm-full-access is required.",
                "full_access_confirmation_required");
        }

        if (_runStarter is null)
        {
            throw new RpcDispatchException(RpcProtocol.InternalError, "run starter is not configured.");
        }

        try
        {
            var outcome = await _runStarter.StartAsync(
                new RunStartRequest(
                    workspace,
                    prompt,
                    preset,
                    OptionalString(request, "sessionId", 64),
                    OptionalBool(request, "trustWorkspace") ?? false,
                    confirmFullAccess,
                    Json: true),
                onSessionCreated: null,
                ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["run_id"] = outcome.RunId,
                ["session_id"] = outcome.SessionId,
                ["status"] = outcome.Status,
                ["workspace_id"] = outcome.WorkspaceId,
            };
        }
        catch (RunStartException ex)
        {
            throw new RpcDispatchException(RpcProtocol.ApplicationError, ex.Line, ex.Code);
        }
    }

    private async Task<JsonNode> RunAttachAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var afterSequence = OptionalLong(request, "afterSequence") ?? 0;
        var includeTransient = OptionalBool(request, "includeTransient") ?? false;

        // Preflight the run before starting a pump: an unknown or expired run id
        // must fail here with a JSON-RPC error, not as a subscription that never
        // delivers an event. The probe shares the dispatch token so a cancelled
        // bridge aborts before a subscription is attached.
        await _agentClient.GetRunAsync(runId, ct).ConfigureAwait(false);

        if (_runBridge is null)
        {
            throw new RpcDispatchException(RpcProtocol.InternalError, "run bridge is not configured.");
        }

        var subscriptionId = _runBridge.Attach(runId, afterSequence, includeTransient);
        return new JsonObject
        {
            ["subscription_id"] = subscriptionId,
            ["run_id"] = runId,
            ["include_transient"] = includeTransient,
        };
    }

    private JsonNode RunDetach(RpcRequest request)
    {
        // The id path is the normal one. The run path is the bounded cleanup for
        // a client whose attach response never arrived: it may own a server-side
        // subscription whose id it never saw, so it asks by run. Zero matches is
        // a valid no-op -- the request promises cleanup, not a known id.
        if (OptionalString(request, "subscriptionId", 64) is { } subscriptionId)
        {
            if (_runBridge is null || !_runBridge.Detach(subscriptionId))
            {
                throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: unknown subscriptionId.");
            }

            return new JsonObject { ["detached"] = subscriptionId };
        }

        var runId = RequireString(request, "runId", 36);
        var detached = _runBridge?.DetachRun(runId) ?? 0;
        return new JsonObject { ["detached"] = detached, ["run_id"] = runId };
    }

    private async Task<JsonNode> RunTrustAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var approve = OptionalBool(request, "approve") ?? false;
        var run = await _agentClient.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(run.TrustRequestId) || string.IsNullOrWhiteSpace(run.DisplayIdentityHash))
        {
            throw new RpcDispatchException(RpcProtocol.ApplicationError, "trust_failed: run has no pending trust challenge", "trust_failed");
        }

        var response = await _agentClient.TrustAsync(
            runId,
            new RuAgent.Protocol.Harness.TrustAgentRunRequest(run.TrustRequestId, run.DisplayIdentityHash, approve ? "approve" : "deny"),
            ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["run_id"] = response.RunId,
            ["status"] = response.Status,
        };
    }

    private async Task<JsonNode> RunStatusAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var run = await _agentClient.GetRunAsync(runId, ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(run, JsonOptions) ?? new JsonObject();
    }

    private async Task<JsonNode> RunOutputAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var payloadId = RequireString(request, "payloadId", 64);
        var payload = await _agentClient.GetPayloadEnvelopeAsync(runId, payloadId, ct).ConfigureAwait(false);
        byte[] bytes;
        switch (payload.Encoding)
        {
            case "utf8text":
            case "json":
                bytes = System.Text.Encoding.UTF8.GetBytes(payload.Content);
                break;
            case "opaque":
                try
                {
                    bytes = Convert.FromBase64String(payload.Content);
                }
                catch (FormatException)
                {
                    throw new RpcDispatchException(RpcProtocol.ApplicationError, "invalid payload encoding", "invalid_payload_encoding");
                }

                break;
            default:
                throw new RpcDispatchException(RpcProtocol.ApplicationError, "unsupported payload encoding", "unsupported_payload_encoding");
        }

        var data = Convert.ToBase64String(bytes);
        if (System.Text.Encoding.UTF8.GetByteCount(data) > RpcProtocol.MaxFrameBytes)
        {
            // The client rejects oversized frames, and a killed transport loses
            // every later event: fail this one call with a named code instead.
            throw new RpcDispatchException(
                RpcProtocol.ApplicationError,
                "output_frame_exceeds_limit: payload exceeds the 1 MiB RPC frame cap; fetch it with the CLI 'agent output' command.",
                ruCode: "output_frame_exceeds_limit");
        }

        return new JsonObject
        {
            ["encoding"] = "base64",
            ["data"] = data,
            ["byte_length"] = bytes.Length,
        };
    }

    private async Task<JsonNode> RunApproveAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var approvalId = RequireString(request, "approvalId", 36);
        var decision = RequireString(request, "decision", 32);
        if (decision is not ("allow_once" or "allow_session_directory" or "allow_always" or "deny"))
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: unknown approval decision.");
        }

        var result = decision == "deny"
            ? await _agentClient.DenyAsync(runId, approvalId, ct).ConfigureAwait(false)
            : await _agentClient.ApproveAsync(runId, approvalId, decision, ct).ConfigureAwait(false);
        return JsonNode.Parse(result.GetRawText()) ?? new JsonObject();
    }

    private async Task<JsonNode> RunCancelAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        await _agentClient.CancelAsync(runId, ct).ConfigureAwait(false);
        return new JsonObject { ["cancelled"] = runId };
    }

    private async Task<JsonNode> RunContinueAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        await _agentClient.ContinueAsync(runId, ct).ConfigureAwait(false);
        return new JsonObject { ["continued"] = runId };
    }

    private async Task<JsonNode> RunCommandAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var command = RequireString(request, "command", 8192);
        var response = await _agentClient.DispatchCommandAsync(runId, command, ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(response, JsonOptions) ?? new JsonObject();
    }

    private async Task<JsonNode> RunReconcileAsync(RpcRequest request, CancellationToken ct)
    {
        var runId = RequireRunId(request);
        var outcome = OptionalString(request, "outcome", 32);
        var hash = OptionalString(request, "hash", 64);
        if (outcome is null || hash is null || hash.Length != 64)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: outcome and a 64-char hash are required.");
        }

        var wireOutcome = outcome switch
        {
            "applied" => "applied",
            "not_applied" or "not-applied" => "not_applied",
            "unknown_continue" or "unknown" => "unknown_continue",
            _ => throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: unknown reconcile outcome."),
        };

        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/reconcile");
        using var http = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { outcome = wireOutcome, snapshot_hash = hash }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        http.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
        var body = await BoundedHttpResponseReader.ReadAsync(response.Content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var (ruCode, message) = MapReconcileFailure(body);
            throw new RpcDispatchException(RpcProtocol.ApplicationError, message, ruCode, (int)response.StatusCode);
        }

        return JsonNode.Parse(body) ?? new JsonObject();
    }

    private static (string RuCode, string Message) MapReconcileFailure(string body)
    {
        string? errorCode = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("error_code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    errorCode = code.GetString();
                }

                if (document.RootElement.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    message = text.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        var bounded = (string.IsNullOrWhiteSpace(message) ? body : message).ReplaceLineEndings(" ").Trim();
        if (bounded.Length > 200)
        {
            bounded = bounded[..200];
        }

        return (string.IsNullOrWhiteSpace(errorCode) ? "reconcile_failed" : errorCode, bounded);
    }

    private static string RequireRunId(RpcRequest request)
    {
        var runId = RequireString(request, "runId", 36);
        if (runId.Length != 36 || !runId.StartsWith("run_", StringComparison.Ordinal))
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: runId must be run_ + 32 lower hex.");
        }

        return runId;
    }

    private async Task<JsonNode> PolicyListAsync(RpcRequest request, CancellationToken ct)
    {
        var scope = OptionalString(request, "scope", 16);
        var policies = await _agentClient.ListPoliciesAsync(scope, null, ct).ConfigureAwait(false);
        var array = new JsonArray();
        foreach (var policy in policies)
        {
            array.Add(JsonNode.Parse(policy.GetRawText()));
        }

        return array;
    }

    private async Task<JsonNode> PolicyImportAsync(RpcRequest request, CancellationToken ct)
    {
        var file = RequireString(request, "file", 4096);
        if (OptionalBool(request, "confirm") != true)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: policy.import requires confirm=true.");
        }

        PolicyFile parsed;
        try
        {
            parsed = PolicyJsonCodec.LoadFromFile(file);
        }
        catch (PolicyJsonException ex)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, $"{ex.ErrorCode}: {ex.Message}", ex.ErrorCode);
        }
        catch (Exception ex)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, $"file_error: {ex.Message}", "file_error");
        }

        var imported = new JsonArray();
        foreach (var rule in parsed.Rules)
        {
            var body = new
            {
                scope = rule.Scope,
                decision = rule.Decision,
                matcher = new
                {
                    tool_id = rule.Matcher.ToolId,
                    tool_version = rule.Matcher.ToolVersion,
                    canonical_path_pattern = rule.Matcher.CanonicalPathPattern,
                    resolved_executable = rule.Matcher.ResolvedExecutable,
                    argv_pattern = rule.Matcher.ArgvPattern,
                    cwd_pattern = rule.Matcher.CwdPattern,
                    operation_class = rule.Matcher.OperationClass,
                    network_intent = rule.Matcher.NetworkIntent,
                },
            };
            var result = await _agentClient.CreatePolicyAsync(body, interactive: true, ct).ConfigureAwait(false);
            imported.Add(JsonNode.Parse(result.GetRawText()));
        }

        return new JsonObject { ["imported"] = imported };
    }

    private async Task<JsonNode> PolicyRemoveAsync(RpcRequest request, CancellationToken ct)
    {
        var ruleId = RequireString(request, "ruleId", 37);
        if (OptionalBool(request, "confirm") != true)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: policy.remove requires confirm=true.");
        }

        await _agentClient.DeletePolicyAsync(ruleId, interactive: true, ct).ConfigureAwait(false);
        return new JsonObject { ["removed"] = ruleId };
    }

    private static string RequireString(RpcRequest request, string name, int maxLength)
    {
        if (!request.Params.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: > 0 } value
            || value.Length > maxLength)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, $"invalid_params: {name} must be a bounded string.");
        }

        return value;
    }

    private static string? OptionalString(RpcRequest request, string name, int maxLength)
    {
        if (!request.Params.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || value.Length > maxLength)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, $"invalid_params: {name} must be a string or null.");
        }

        return value;
    }

    private static int RequireInt(RpcRequest request, string name)
    {
        if (!request.Params.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, $"invalid_params: {name} must be an integer.");
        }

        return value;
    }

    private static bool? OptionalBool(RpcRequest request, string name)
    {
        if (!request.Params.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new RpcDispatchException(RpcProtocol.InvalidParams, $"invalid_params: {name} must be a boolean."),
        };
    }

    private static long? OptionalLong(RpcRequest request, string name)
    {
        if (!request.Params.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) || value < 0)
        {
            throw new RpcDispatchException(RpcProtocol.InvalidParams, $"invalid_params: {name} must be a non-negative integer.");
        }

        return value;
    }
}
