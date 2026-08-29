using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Observation;

public sealed class ObservationRuntimeRequestHandler : IPrivateRuntimeRequestHandler
{
    private const int MaxImageBase64Chars = 5_600_000;
    private const int MaxRequestBodyBytes = 6 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IObservationProvider _provider;

    public ObservationRuntimeRequestHandler(IObservationProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PathAndQuery.Split('?', 2)[0];
        if (request.Method != "POST" || path != "/observation/analyze")
        {
            throw new PrivateRuntimeRequestException("unsupported_route");
        }

        if (request.Body.Length > MaxRequestBodyBytes)
        {
            return InvalidRequest();
        }

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(StrictUtf8.GetString(request.Body.Span)) as JsonObject
                ?? throw new JsonException();
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException)
        {
            return InvalidRequest();
        }

        if (!TryReadRequest(payload, out var observationRequest))
        {
            return InvalidRequest();
        }

        try
        {
            var observation = await _provider.AnalyzeAsync(observationRequest, cancellationToken).ConfigureAwait(false);
            if (observation is null)
            {
                return JsonResponse(200, new JsonObject { ["status"] = "skipped", ["reason"] = "no_observation" });
            }

            return JsonResponse(
                200,
                new JsonObject
                {
                    ["status"] = "ok",
                    ["scene"] = observation.Scene,
                    ["event"] = observation.Event,
                    ["confidence"] = observation.Confidence,
                    ["novelty"] = observation.Novelty,
                    ["comment_worthiness"] = observation.CommentWorthiness,
                    ["visible_text"] = new JsonArray(observation.VisibleText.Select(item => (JsonNode?)item).ToArray()),
                    ["reaction"] = observation.Reaction,
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObservationProviderException error)
        {
            return error.Code == "observation_skipped"
                ? JsonResponse(200, new JsonObject
                {
                    ["status"] = "skipped",
                    ["reason"] = error.Reason ?? "provider_unavailable",
                })
                : JsonResponse(200, new JsonObject { ["status"] = "error", ["message"] = "Observation failed." });
        }
        catch
        {
            return JsonResponse(200, new JsonObject { ["status"] = "error", ["message"] = "Observation failed." });
        }
    }

    private static bool TryReadRequest(JsonObject payload, out ObservationRequest request)
    {
        request = null!;
        if (payload["image_b64"] is not JsonValue imageValue
            || !imageValue.TryGetValue<string>(out var image)
            || string.IsNullOrWhiteSpace(image)
            || image.Length > MaxImageBase64Chars
            || image.Any(char.IsWhiteSpace)
            || payload["prompt"] is not JsonValue promptValue
            || !promptValue.TryGetValue<string>(out var prompt)
            || string.IsNullOrWhiteSpace(prompt)
            || Encoding.UTF8.GetByteCount(prompt) > RuntimeLimits.MaxObservationPromptBytes
            || (payload["model_name"] is JsonValue modelValue
                && (!modelValue.TryGetValue<string>(out var modelName)
                    || string.IsNullOrWhiteSpace(modelName)
                    || modelName.Length > RuntimeLimits.MaxModelNameChars)))
        {
            return false;
        }

        var model = payload["model_name"] is JsonValue selected
            && selected.TryGetValue<string>(out var selectedModel)
            ? selectedModel
            : GeminiVisionOptions.DefaultModel;
        try
        {
            var bytes = Convert.FromBase64String(image);
            if (bytes.Length == 0 || bytes.Length > RuntimeLimits.MaxObservationImageBytes)
            {
                return false;
            }

            request = new ObservationRequest(bytes, prompt, model);
            return true;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static PrivateRuntimeResponse InvalidRequest() => JsonResponse(
        422,
        new JsonObject { ["detail"] = "Invalid runtime request." });

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) =>
        new(statusCode, "application/json", StrictUtf8.GetBytes(payload.ToJsonString()));
}
