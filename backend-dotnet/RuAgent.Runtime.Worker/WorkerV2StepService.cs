using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

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
    private readonly SemaphoreSlim _providerGate = new(1, 1);

    public WorkerV2StepService(IModelProvider provider, WorkerV2Options? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? DefaultOptions;
    }

    public async Task<WorkerV2StepResult> RunAsync(JsonObject startPayload, CancellationToken cancellationToken)
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

        await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            var visibleText = calls.Count > 0 ? null : (text.Length == 0 ? null : text.ToString());
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
            _providerGate.Release();
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

    private static WorkerV2StepException MapProviderException(ModelProviderException error) => error.Code switch
    {
        "timeout" => new WorkerV2StepException("provider_timeout", retryable: true, error),
        "auth_failed" or "forbidden" => new WorkerV2StepException("provider_auth", retryable: false, error),
        "rate_limited" or "server_error" or "transport_error" => new WorkerV2StepException("worker_unavailable", error.Retryable, error),
        "malformed_payload" or "response_too_large" => new WorkerV2StepException("protocol_failure", retryable: false, error),
        _ => new WorkerV2StepException("generation_failed", error.Retryable, error),
    };
}
