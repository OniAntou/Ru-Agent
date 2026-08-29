using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Audio;

/// <summary>
/// Handles the private HTTP voice routes that are relayed through Worker v1.
/// Public ASP.NET endpoints remain owned by RuBackend; this handler only emits
/// bounded JSON or WAV responses and never forwards provider exception text.
/// </summary>
public sealed class VoiceRuntimeRequestHandler : IPrivateRuntimeRequestHandler
{
    private const int MaxRequestBodyBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IVoiceProvider _provider;
    private readonly IVoiceLifecycle? _lifecycle;

    public VoiceRuntimeRequestHandler(IVoiceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _lifecycle = provider as IVoiceLifecycle;
    }

    public async Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PathAndQuery.Split('?', 2)[0];
        return (request.Method, path) switch
        {
            ("GET", "/voice/status") => StatusResponse(),
            ("POST", "/voice/latency") => LatencyResponse(request),
            ("POST", "/voice/tts") => await SynthesizeAsync(request, cancellationToken).ConfigureAwait(false),
            ("POST", "/voice/warmup") => await WarmupAsync(request, cancellationToken).ConfigureAwait(false),
            ("POST", "/voice/release") => await ReleaseAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new PrivateRuntimeRequestException("unsupported_route"),
        };
    }

    private static PrivateRuntimeResponse StatusResponse() => JsonResponse(
        200,
        new JsonObject
        {
            ["status"] = "ok",
            ["available"] = true,
            ["voice_label"] = "Ru (OmniVoice)",
            ["voice_style_id"] = "original",
            ["voice_speed"] = 1.0,
            ["auto_pace_enabled"] = false,
            ["auto_read_replies"] = true,
            ["show_subtitles"] = false,
            ["subtitle_color"] = "#FFFFFF",
            ["subtitle_size"] = "medium",
            ["subtitle_duration"] = 2.0,
            ["subtitle_pos_mode"] = "auto",
            ["subtitle_pos_x"] = -1,
            ["subtitle_pos_y"] = -1,
        });

    private static PrivateRuntimeResponse LatencyResponse(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var outcome = ReadText(payload, "outcome", 32);
        if (outcome is not ("completed" or "cancelled" or "failed"))
        {
            return InvalidRequest();
        }

        foreach (var property in payload)
        {
            if (property.Key == "outcome")
            {
                continue;
            }

            if (property.Value is not JsonValue value
                || !value.TryGetValue<double>(out var number)
                || !double.IsFinite(number)
                || number is < 0 or > 120_000)
            {
                return InvalidRequest();
            }
        }

        return JsonResponse(200, new JsonObject { ["status"] = "ok" });
    }

    private async Task<PrivateRuntimeResponse> SynthesizeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var text = ReadText(payload, "text", 12_000)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = "Voice text is required.",
            });
        }

        var voiceLabel = ReadText(payload, "voice_label", 128) ?? "Ru (OmniVoice)";
        var speed = ReadNumber(payload, "speed") ?? 1.0;
        if (!double.IsFinite(speed) || speed is < 0.5 or > 2.0)
        {
            return InvalidRequest();
        }

        VoiceRequest voiceRequest;
        try
        {
            voiceRequest = new VoiceRequest(text, voiceLabel, speed, "original");
        }
        catch (ArgumentException)
        {
            return InvalidRequest();
        }

        try
        {
            var audio = await _provider.SynthesizeAsync(voiceRequest, cancellationToken).ConfigureAwait(false);
            return new PrivateRuntimeResponse(
                200,
                "audio/wav",
                audio.WavBytes,
                binary: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VoiceProviderException error)
        {
            return VoiceError(error.Code);
        }
        catch
        {
            return VoiceError("voice_failed");
        }
    }

    private async Task<PrivateRuntimeResponse> WarmupAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        if (_lifecycle is null)
        {
            return VoiceError("voice_unavailable");
        }

        var payload = ParseObject(request, allowEmpty: true);
        var voiceLabel = ReadText(payload, "voice_label", 128) ?? "Ru (OmniVoice)";
        try
        {
            await _lifecycle.WarmupAsync(voiceLabel, cancellationToken).ConfigureAwait(false);
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "ok",
                ["result"] = new JsonObject
                {
                    ["ready"] = true,
                    ["voice_profile"] = ProfileFor(voiceLabel),
                },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VoiceProviderException error)
        {
            return VoiceError(error.Code);
        }
        catch
        {
            return VoiceError("voice_failed");
        }
    }

    private async Task<PrivateRuntimeResponse> ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_lifecycle is null)
        {
            return VoiceError("voice_unavailable");
        }

        try
        {
            await _lifecycle.ReleaseAsync(cancellationToken).ConfigureAwait(false);
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "ok",
                ["result"] = new JsonObject { ["released"] = true },
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VoiceProviderException error)
        {
            return VoiceError(error.Code);
        }
        catch
        {
            return VoiceError("voice_failed");
        }
    }

    private static JsonObject ParseObject(PrivateRuntimeRequest request, bool allowEmpty)
    {
        if (request.Body.Length > MaxRequestBodyBytes)
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }

        if (request.Body.IsEmpty && allowEmpty)
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(StrictUtf8.GetString(request.Body.Span)) as JsonObject
                ?? throw new JsonException();
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException)
        {
            throw new PrivateRuntimeRequestException("invalid_request", false, error);
        }
    }

    private static string? ReadText(JsonObject payload, string key, int maximum)
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
            throw new PrivateRuntimeRequestException("invalid_request");
        }

        return text;
    }

    private static double? ReadNumber(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value
            && value.TryGetValue<double>(out var number)
            && double.IsFinite(number))
        {
            return number;
        }

        throw new PrivateRuntimeRequestException("invalid_request");
    }

    private static string ProfileFor(string voiceLabel) => voiceLabel.Trim() switch
    {
        "ru" or "Ru (OmniVoice)" => "ru",
        "ai_vy" or "Ái Vy (OmniVoice)" => "ai_vy",
        _ => throw new VoiceProviderException("unsupported_voice", retryable: false),
    };

    private static PrivateRuntimeResponse VoiceError(string code) => JsonResponse(
        code == "voice_unavailable" ? 503 : 200,
        new JsonObject
        {
            ["status"] = "error",
            ["message"] = code == "voice_unavailable"
                ? "Voice runtime is unavailable."
                : "Voice runtime failed.",
        });

    private static PrivateRuntimeResponse InvalidRequest() => JsonResponse(
        422,
        new JsonObject { ["detail"] = "Invalid voice request." });

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) => new(
        statusCode,
        "application/json",
        StrictUtf8.GetBytes(payload.ToJsonString()));
}
