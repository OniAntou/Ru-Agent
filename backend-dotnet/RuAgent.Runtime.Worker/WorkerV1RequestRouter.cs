using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Adapters.Speech;
using RuAgent.Runtime.Adapters.Devices;
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
    private readonly VoiceStreamCoordinator? _voice;
    private readonly IVtubeStudioClient? _vts;

    public ManagedWorkerV1RequestRouter(
        ManagedRuntimeSettingsService settings,
        PrivateRuntimeRequestRouter privateRouter,
        VoiceStreamCoordinator? voice = null,
        ISpeechRecognizer? speech = null,
        IVtubeStudioClient? vts = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(privateRouter);
        _privateRouter = speech is null
            ? privateRouter
            : privateRouter.WithRouteHandler(
                "POST",
                "/voice/transcribe",
                new SpeechRuntimeRequestHandler(speech));
        _voice = voice;
        _vts = vts;
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
            case WorkerV1Contracts.VoiceStart:
                await foreach (var reply in HandleVoiceAsync(payload, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return reply;
                }

                yield break;
            case WorkerV1Contracts.VtsCatalog:
                yield return await HandleVtsCatalogAsync(payload, cancellationToken).ConfigureAwait(false);
                yield break;
            case WorkerV1Contracts.VtsAction:
                yield return await HandleVtsActionAsync(payload, cancellationToken).ConfigureAwait(false);
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

    private async Task<WorkerV1Reply> HandleVtsCatalogAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        if (_vts is null)
        {
            return Error("vts_unavailable", retryable: true);
        }

        try
        {
            if (!string.Equals(OptionalText(payload, "operation", 32), "catalog", StringComparison.Ordinal))
            {
                return Error("invalid_request", retryable: false);
            }

            var catalog = await _vts.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            return new WorkerV1Reply(
                WorkerV1Contracts.VtsCatalog,
                new JsonObject
                {
                    ["model_id"] = catalog.ModelId,
                    ["model_name"] = catalog.ModelName,
                    ["hotkey_codes"] = new JsonArray(catalog.HotkeyCodes
                        .OrderBy(code => code, StringComparer.Ordinal)
                        .Select(code => (JsonNode?)code)
                        .ToArray()),
                    ["expression_codes"] = new JsonArray(catalog.ExpressionCodes
                        .OrderBy(code => code, StringComparer.Ordinal)
                        .Select(code => (JsonNode?)code)
                        .ToArray()),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VtubeStudioProviderException error)
        {
            return Error(error.Code, error.Retryable);
        }
        catch (ArgumentException)
        {
            return Error("invalid_request", retryable: false);
        }
        catch
        {
            return Error("vts_catalog_failed", retryable: true);
        }
    }

    private async Task<WorkerV1Reply> HandleVtsActionAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        if (_vts is null)
        {
            return Error("vts_unavailable", retryable: true);
        }

        try
        {
            if (!string.Equals(OptionalText(payload, "operation", 32), "action", StringComparison.Ordinal)
                || payload["codes"] is not JsonArray codeArray
                || codeArray.Count is < 1 or > RuntimeLimits.MaxVtsActionCodes)
            {
                return Error("invalid_request", retryable: false);
            }

            var codes = codeArray.Select(node => node is JsonValue value && value.TryGetValue<string>(out var code)
                    ? code
                    : throw new ArgumentException("VTube Studio action code is invalid.", nameof(payload)))
                .ToArray();
            var catalog = await _vts.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> normalized;
            try
            {
                normalized = catalog.ValidateActionCodes(codes);
            }
            catch (ArgumentException)
            {
                return Error("unknown_vts_code", retryable: false);
            }

            await _vts.TriggerAsync(normalized, cancellationToken).ConfigureAwait(false);
            return new WorkerV1Reply(
                WorkerV1Contracts.VtsAction,
                new JsonObject { ["status"] = "ok" });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VtubeStudioProviderException error)
        {
            return Error(error.Code, error.Retryable);
        }
        catch (ArgumentException)
        {
            return Error("invalid_request", retryable: false);
        }
        catch
        {
            return Error("vts_action_failed", retryable: true);
        }
    }

    private async IAsyncEnumerable<WorkerV1Reply> HandleVoiceAsync(
        JsonObject payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_voice is null)
        {
            yield return Error("voice_unavailable", retryable: true);
            yield break;
        }

        if (!TryBuildVoiceRequest(payload, out var requestId, out var request))
        {
            yield return Error("invalid_request", retryable: false);
            yield break;
        }

        yield return new WorkerV1Reply(
            WorkerV1Contracts.VoiceControl,
            new JsonObject
            {
                ["type"] = "started",
                ["request_id"] = requestId,
                ["sequence"] = 0,
            });

        var capture = await ConsumeVoiceAsync(_voice, request, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in capture.Chunks)
        {
            if (chunk.State == "audio")
            {
                yield return new WorkerV1Reply(
                    WorkerV1Contracts.VoiceControl,
                    new JsonObject
                    {
                        ["type"] = "audio",
                        ["request_id"] = requestId,
                        ["sequence"] = chunk.Sequence,
                        ["state"] = "audio_ready",
                    },
                    chunk.Audio);
            }
            else if (chunk.State == "ended")
            {
                yield return new WorkerV1Reply(
                    WorkerV1Contracts.VoiceDone,
                    new JsonObject
                    {
                        ["type"] = "ended",
                        ["request_id"] = requestId,
                        ["sequence"] = chunk.Sequence,
                        ["state"] = "done",
                    });
            }
        }

        if (capture.Error is VoiceProviderException error)
        {
            yield return Error(error.Code, error.Retryable);
        }
        else if (capture.Error is not null)
        {
            yield return Error("voice_failed", retryable: true);
        }
    }

    private static async Task<VoiceCapture> ConsumeVoiceAsync(
        VoiceStreamCoordinator voice,
        VoiceRequest request,
        CancellationToken cancellationToken)
    {
        var chunks = new List<VoiceChunk>();
        try
        {
            await foreach (var chunk in voice.StreamAsync(request, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }

            return new VoiceCapture(chunks, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new VoiceCapture(chunks, error);
        }
    }

    private static bool TryBuildVoiceRequest(
        JsonObject payload,
        out string requestId,
        out VoiceRequest request)
    {
        requestId = string.Empty;
        request = null!;
        try
        {
            requestId = RequiredText(payload, "request_id", 128);
            var text = RequiredText(payload, "text", RuntimeLimits.MaxVoiceTextBytes);
            var voiceLabel = OptionalText(payload, "voice_label", 128) ?? "Ru (OmniVoice)";
            var speed = OptionalDouble(payload, "speed") ?? 1.0;
            var style = OptionalText(payload, "voice_style_id", 32) ?? "original";
            request = new VoiceRequest(text, voiceLabel, speed, style);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
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

    private static double? OptionalDouble(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value
            && value.TryGetValue<double>(out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        if (node is JsonValue integerValue
            && integerValue.TryGetValue<int>(out var integer)
            && double.IsFinite(integer))
        {
            return integer;
        }

        throw new ArgumentException("Runtime number field is invalid.", nameof(payload));
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

    private sealed record VoiceCapture(
        IReadOnlyList<VoiceChunk> Chunks,
        Exception? Error);
}
