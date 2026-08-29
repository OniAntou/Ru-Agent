using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Core.Lifecycle;

namespace RuAgent.Runtime.Worker;

public sealed record WorkerV2StepResult(
    string? Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    string FinishReason,
    SystemPromptReceipt PromptReceipt);

public sealed class WorkerV2StepException : Exception
{
    public WorkerV2StepException(string code, bool retryable, Exception? innerException = null)
        : base($"worker_v2_{code}", innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

public sealed class WorkerV2StepService
{
    private static readonly WorkerV2Options DefaultOptions = new(
        0,
        "test-worker",
        new Uri("http://127.0.0.1"),
        "managed-agent",
        string.Empty);
    private readonly IModelProvider _provider;
    private readonly WorkerV2Options _options;
    private readonly RuntimeLeaseCoordinator? _leases;
    private readonly SemaphoreSlim _providerGate = new(1, 1);

    public WorkerV2StepService(
        IModelProvider provider,
        WorkerV2Options? options = null,
        RuntimeLeaseCoordinator? leases = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? DefaultOptions;
        _leases = leases;
    }

    /// <summary>
    /// The coordinator is shared with the v1 device adapters by the managed
    /// composition. Generation holds the observation lease so an observation
    /// request can fail fast with <c>generation_busy</c> instead of competing
    /// with the active model call.
    /// </summary>
    public RuntimeLeaseCoordinator? Leases => _leases;

    public async Task<WorkerV2StepResult> RunAsync(JsonObject startPayload, CancellationToken cancellationToken)
        => await RunAsync(startPayload, null, cancellationToken).ConfigureAwait(false);

    public async Task<WorkerV2StepResult> RunAsync(
        JsonObject startPayload,
        Func<string, ValueTask>? onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startPayload);
        WorkerV2StartRequest start;
        try
        {
            start = WorkerV2PromptCodec.ReadStart(startPayload);
        }
        catch (RuAgent.Protocol.AgentProtocolException error)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false, error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SystemPromptComposer.Compose(start.Context);
        var request = new ModelRequest(
            _options.ProviderModel,
            snapshot.Text,
            start.Prompt,
            start.Turns,
            start.Settings,
            _options.ProviderEndpoint,
            _options.ProviderCredential);
        var holdback = new DeltaHoldback(onDelta);

        RuntimeLease? generationLease = null;
        var providerGateEntered = false;
        try
        {
            if (_leases is not null)
            {
                generationLease = await _leases
                    .AcquireAsync(ResourceKind.Observation, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            providerGateEntered = true;
            var text = new StringBuilder();
            var toolFragments = new List<ModelToolFragment>();
            string? finishReason = null;
            await foreach (var delta in _provider.GenerateAsync(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (delta.Text is not null)
                {
                    text.Append(delta.Text);
                    if (text.Length > 1_000_000)
                    {
                        throw new WorkerV2StepException("protocol_failure", retryable: false);
                    }

                    await holdback.ForwardAsync(delta.Text).ConfigureAwait(false);
                }

                toolFragments.AddRange(delta.ToolFragments);
                if (toolFragments.Count > 16)
                {
                    throw new WorkerV2StepException("protocol_failure", retryable: false);
                }

                if (delta.FinishReason is not null)
                {
                    finishReason = delta.FinishReason;
                }

                if (delta.Completed)
                {
                    break;
                }
            }

            finishReason ??= toolFragments.Count > 0 ? "tool_calls" : "stop";
            var calls = ParseToolCalls(toolFragments, start.Settings, finishReason, text);
            if (calls.Count > 0)
            {
                finishReason = "tool_calls";
            }
            var visibleText = calls.Count > 0 ? null : (text.Length == 0 ? null : text.ToString());
            if (calls.Count == 0)
            {
                await holdback.ReleaseAsync().ConfigureAwait(false);
            }

            return new WorkerV2StepResult(visibleText, calls, finishReason, snapshot.Receipt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkerV2StepException)
        {
            throw;
        }
        catch (ModelProviderException error)
        {
            throw MapProviderException(error);
        }
        catch (Exception error)
        {
            throw new WorkerV2StepException("generation_failed", retryable: true, error);
        }
        finally
        {
            if (providerGateEntered)
            {
                _providerGate.Release();
            }

            if (generationLease is not null)
            {
                await generationLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static List<ModelToolCall> ParseToolCalls(
        IReadOnlyList<ModelToolFragment> fragments,
        JsonObject settings,
        string finishReason,
        StringBuilder text)
    {
        if (fragments.Count == 0)
        {
            var promptSelection = ParsePromptToolSelection(text.ToString(), settings);
            if (promptSelection is not null)
            {
                return [promptSelection];
            }

            if (finishReason == "tool_calls")
            {
                throw new WorkerV2StepException("protocol_failure", retryable: false);
            }

            return new List<ModelToolCall>();
        }

        if (fragments.Count > 1 && fragments.Select(fragment => fragment.ProviderCallId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false);
        }

        if (finishReason != "tool_calls" || text.Length != 0)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false);
        }

        var toolName = fragments.Select(fragment => fragment.ToolName).FirstOrDefault(name => !string.IsNullOrEmpty(name));
        if (string.IsNullOrEmpty(toolName))
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false);
        }

        var tool = settings["model_tools"] is JsonArray modelTools
            ? modelTools.OfType<JsonObject>().FirstOrDefault(item => item["tool_id"]?.GetValue<string>() == toolName)
            : null;
        var toolVersion = tool?["tool_version"]?.GetValue<string>();
        if (toolVersion is null)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false);
        }

        var argumentsText = string.Concat(fragments.Select(fragment => fragment.ArgumentsFragment));
        if (Encoding.UTF8.GetByteCount(argumentsText) > 64 * 1024)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false);
        }

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(argumentsText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("arguments must be an object");
            }

            arguments = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false, error);
        }

        try
        {
            return new List<ModelToolCall>
            {
                new(fragments[0].ProviderCallId, toolName, toolVersion, arguments),
            };
        }
        catch (ArgumentException error)
        {
            throw new WorkerV2StepException("protocol_failure", retryable: false, error);
        }
    }

    private static ModelToolCall? ParsePromptToolSelection(string text, JsonObject settings)
    {
        var candidate = text.Trim();
        if (candidate.Length == 0 || candidate[0] is not ('{' or '['))
        {
            return null;
        }

        var modelTools = settings["model_tools"] as JsonArray;
        var hasTools = modelTools is { Count: > 0 };
        if (!hasTools && IsJsonObjectWithoutToolFields(candidate))
        {
            return null;
        }

        var advertised = modelTools?
            .OfType<JsonObject>()
            .Select(tool => tool["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                    ? id
                    : string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        ModelToolSelection selection;
        try
        {
            selection = ModelToolSelectionParser.Parse(candidate, advertised);
        }
        catch (ModelToolSelectionException error) when (
            error.Code is "unknown_tool" or "invalid_tool_version")
        {
            throw new WorkerV2StepException("capability_unavailable", retryable: false, error);
        }
        catch (ModelToolSelectionException error)
        {
            throw new WorkerV2StepException("invalid_selection", retryable: false, error);
        }

        var configuredTool = modelTools?
            .OfType<JsonObject>()
            .FirstOrDefault(tool => tool["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                && string.Equals(id, selection.ToolId, StringComparison.Ordinal));
        var configuredVersion = configuredTool?["tool_version"] is JsonValue version
            && version.TryGetValue<string>(out var versionText)
                ? versionText
                : null;
        if (!string.Equals(configuredVersion, selection.ToolVersion, StringComparison.Ordinal))
        {
            throw new WorkerV2StepException("capability_unavailable", retryable: false);
        }

        try
        {
            return new ModelToolCall(null, selection.ToolId, selection.ToolVersion, selection.Arguments);
        }
        catch (ArgumentException error)
        {
            throw new WorkerV2StepException("invalid_selection", retryable: false, error);
        }
    }

    private static bool IsJsonObjectWithoutToolFields(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return !document.RootElement.EnumerateObject().Any(property =>
                property.Name is "tool_id" or "tool_version" or "arguments");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static WorkerV2StepException MapProviderException(ModelProviderException error) => error.Code switch
    {
        "timeout" => new WorkerV2StepException("provider_timeout", retryable: true, error),
        "auth_failed" or "forbidden" => new WorkerV2StepException("provider_auth", retryable: false, error),
        "rate_limited" or "server_error" or "transport_error" => new WorkerV2StepException("worker_unavailable", error.Retryable, error),
        "malformed_payload" or "response_too_large" => new WorkerV2StepException("protocol_failure", retryable: false, error),
        _ => new WorkerV2StepException("generation_failed", error.Retryable, error),
    };

    private sealed class DeltaHoldback
    {
        private readonly Func<string, ValueTask>? _sink;
        private readonly List<string> _held = [];
        private bool _decided;
        private bool _structured;
        private long _bytes;

        public DeltaHoldback(Func<string, ValueTask>? sink)
        {
            _sink = sink;
        }

        public async ValueTask ForwardAsync(string delta)
        {
            if (_sink is null || delta.Length == 0)
            {
                return;
            }

            _bytes += Encoding.UTF8.GetByteCount(delta);
            if (_bytes > 1_000_000)
            {
                throw new WorkerV2StepException("protocol_failure", retryable: false);
            }

            if (_decided)
            {
                if (!_structured)
                {
                    await _sink(delta).ConfigureAwait(false);
                }

                return;
            }

            _held.Add(delta);
            var combined = string.Concat(_held);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return;
            }

            _decided = true;
            var first = combined.TrimStart()[0];
            if (first is '{' or '[')
            {
                _structured = true;
                return;
            }

            foreach (var held in _held)
            {
                await _sink(held).ConfigureAwait(false);
            }

            _held.Clear();
        }

        public async ValueTask ReleaseAsync()
        {
            if (_sink is not null && _structured)
            {
                foreach (var held in _held)
                {
                    await _sink(held).ConfigureAwait(false);
                }
            }

            _held.Clear();
            _decided = true;
            _structured = false;
        }
    }
}
