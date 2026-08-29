using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Worker;

public interface IWorkerV1RequestRouter
{
    IAsyncEnumerable<WorkerV1Reply> RouteAsync(
        string type,
        JsonObject payload,
        CancellationToken cancellationToken);
}

public sealed class UnavailableWorkerV1RequestRouter : IWorkerV1RequestRouter
{
    public async IAsyncEnumerable<WorkerV1Reply> RouteAsync(
        string type,
        JsonObject payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var code = type switch
        {
            WorkerV1Contracts.RuntimeLoad or WorkerV1Contracts.RuntimeRequest => "runtime_unavailable",
            WorkerV1Contracts.VoiceStart => "voice_unavailable",
            WorkerV1Contracts.VtsCatalog or WorkerV1Contracts.VtsAction => "vts_unavailable",
            _ => "unsupported_operation",
        };
        yield return new WorkerV1Reply(
            WorkerV1Contracts.Error,
            new JsonObject
            {
                ["code"] = code,
                ["retryable"] = true,
            });
    }
}

public sealed class ManagedWorkerV1RequestRouter : IWorkerV1RequestRouter
{
    private const int MaxRuntimeBodyBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> SensitiveKeys = new HashSet<string>(
        [
            "api_key",
            "opencode_api_key",
            "kilo_api_key",
            "gemini_vision_api_key",
            "custom_api_key",
            "token",
            "secret",
            "password",
            "credential",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly ManagedRuntimeSettingsService _settings;
    private readonly PrivateRuntimeRequestRouter _privateRouter;

    public ManagedWorkerV1RequestRouter(
        ManagedRuntimeSettingsService settings,
        PrivateRuntimeRequestRouter privateRouter)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _privateRouter = privateRouter ?? throw new ArgumentNullException(nameof(privateRouter));
    }

    public async IAsyncEnumerable<WorkerV1Reply> RouteAsync(
        string type,
        JsonObject payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);

        switch (type)
        {
            case WorkerV1Contracts.RuntimeLoad:
                yield return await HandleRuntimeLoadAsync(payload, cancellationToken).ConfigureAwait(false);
                yield break;
            case WorkerV1Contracts.RuntimeRequest:
                yield return await HandleRuntimeRequestAsync(payload, cancellationToken).ConfigureAwait(false);
                yield break;
            default:
                yield return Error(
                    type is WorkerV1Contracts.VoiceStart
                        ? "voice_unavailable"
                        : type is WorkerV1Contracts.VtsCatalog or WorkerV1Contracts.VtsAction
                            ? "vts_unavailable"
                            : "unsupported_operation",
                    retryable: true);
                yield break;
        }
    }

    private async Task<WorkerV1Reply> HandleRuntimeLoadAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        try
        {
            RejectSensitiveKeys(payload);
            var input = ReadRuntimeSettings(payload);
            var message = await _settings.LoadAsync(input, cancellationToken).ConfigureAwait(false);
            return new WorkerV1Reply(
                WorkerV1Contracts.RuntimeState,
                new JsonObject
                {
                    ["status"] = "success",
                    ["message"] = message,
                    ["backend"] = input.Backend,
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManagedRuntimeSettingsException error)
        {
            return Error(error.Code, error.Retryable);
        }
        catch (ArgumentException)
        {
            return Error("invalid_request", retryable: false);
        }
    }

    private async Task<WorkerV1Reply> HandleRuntimeRequestAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = ReadPrivateRequest(payload);
            var response = await _privateRouter.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            var responsePayload = new JsonObject
            {
                ["status_code"] = response.StatusCode,
                ["content_type"] = response.ContentType,
                ["headers"] = Headers(response.Headers),
            };
            if (response.Binary)
            {
                responsePayload["binary"] = true;
                return new WorkerV1Reply(
                    WorkerV1Contracts.RuntimeResponse,
                    responsePayload,
                    response.Body);
            }

            responsePayload["body"] = StrictUtf8.GetString(response.Body.Span);
            return new WorkerV1Reply(WorkerV1Contracts.RuntimeResponse, responsePayload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PrivateRuntimeRequestException error)
        {
            return Error(error.Code, error.Retryable);
        }
        catch (DecoderFallbackException)
        {
            return Error("runtime_request_failed", retryable: true);
        }
        catch (ArgumentException)
        {
            return Error("invalid_request", retryable: false);
        }
    }

    private static RuntimeSettingsInput ReadRuntimeSettings(JsonObject payload)
    {
        var backend = RequiredText(payload, "backend", 64);
        var modelKey = backend switch
        {
            "OpenCode Zen" => "opencode_model",
            "Kilo Gateway" => "kilo_model",
            "Ollama" => "ollama_model",
            "LM Studio" => "lmstudio_model",
            "Custom OpenAI" => "custom_model",
            _ => throw new ArgumentException("Unsupported runtime backend.", nameof(payload)),
        };
        var urlKey = backend switch
        {
            "Ollama" => "ollama_url",
            "LM Studio" => "lmstudio_url",
            "Custom OpenAI" => "custom_url",
            _ => null,
        };
        var model = RequiredText(payload, modelKey, RuntimeLimits.MaxModelNameChars);
        var baseUrl = urlKey is null ? null : OptionalText(payload, urlKey, 2_048);
        var apiVariant = OptionalText(payload, "api_variant", 64) ?? string.Empty;
        var reasoning = OptionalBoolean(payload, "kilo_reasoning_enabled");
        return new RuntimeSettingsInput(backend, model, baseUrl, apiVariant, reasoning);
    }

    private static PrivateRuntimeRequest ReadPrivateRequest(JsonObject payload)
    {
        var method = RequiredText(payload, "method", 16);
        var path = RequiredText(payload, "path_and_query", PrivateRuntimeRequestRouter.MaxPathAndQueryChars);
        var contentType = OptionalText(payload, "content_type", 256);
        var bodyText = OptionalText(payload, "body", MaxRuntimeBodyBytes) ?? string.Empty;
        if (StrictUtf8.GetByteCount(bodyText) > MaxRuntimeBodyBytes)
        {
            throw new ArgumentException("Runtime body is too large.", nameof(payload));
        }

        return new PrivateRuntimeRequest(
            method,
            path,
            contentType,
            StrictUtf8.GetBytes(bodyText));
    }

    private static string RequiredText(JsonObject payload, string key, int maximum)
    {
        var value = OptionalText(payload, key, maximum);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Runtime request field is required.", nameof(payload));
    }

    private static string? OptionalText(JsonObject payload, string key, int maximum)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || text.Length > maximum
            || text.Any(char.IsControl))
        {
            throw new ArgumentException("Runtime request field is invalid.", nameof(payload));
        }

        return text;
    }

    private static bool OptionalBoolean(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException("Runtime boolean field is invalid.", nameof(payload));
    }

    private static void RejectSensitiveKeys(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (SensitiveKeys.Contains(property.Key.Trim()))
                {
                    throw new ArgumentException("Sensitive runtime fields are not accepted.", nameof(node));
                }

                RejectSensitiveKeys(property.Value!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    RejectSensitiveKeys(child);
                }
            }
        }
    }

    private static JsonObject Headers(IReadOnlyDictionary<string, string>? headers)
    {
        var result = new JsonObject();
        if (headers is null)
        {
            return result;
        }

        foreach (var header in headers)
        {
            result[header.Key] = header.Value;
        }

        return result;
    }

    private static WorkerV1Reply Error(string code, bool retryable) => new(
        WorkerV1Contracts.Error,
        new JsonObject
        {
            ["code"] = code,
            ["retryable"] = retryable,
        });
}
