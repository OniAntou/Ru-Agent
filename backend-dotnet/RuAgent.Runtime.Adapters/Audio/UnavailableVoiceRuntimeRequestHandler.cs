using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;

namespace RuAgent.Runtime.Adapters.Audio;

/// <summary>
/// Explicit fallback for installations that do not ship the isolated
/// OmniVoice provider assets. Keeping voice routes concrete prevents the
/// managed Worker from exposing the generic runtime-route fallback.
/// </summary>
public sealed class UnavailableVoiceRuntimeRequestHandler : IPrivateRuntimeRequestHandler
{
    private const int MaxRequestBodyBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> AllowedLatencyFields =
        new HashSet<string>(
            [
                "submit_to_first_delta_ms",
                "submit_to_first_phrase_ms",
                "phrase_to_first_audio_ms",
                "submit_to_playback_requested_ms",
                "outcome",
            ],
            StringComparer.Ordinal);

    public Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PathAndQuery.Split('?', 2)[0];
        return (request.Method, path) switch
        {
            ("GET", "/voice/status") => Task.FromResult(JsonResponse(200, new JsonObject
            {
                ["status"] = "ok",
                ["available"] = false,
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
            })),
            ("POST", "/voice/latency") => Task.FromResult(Latency(request)),
            ("POST", "/voice/tts") => Task.FromResult(Tts(request)),
            ("POST", "/voice/warmup") => Task.FromResult(LifecycleUnavailable(request)),
            ("POST", "/voice/release") => Task.FromResult(LifecycleUnavailable(request)),
            _ => throw new PrivateRuntimeRequestException("unsupported_route"),
        };
    }

    private static PrivateRuntimeResponse Latency(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        if (payload.Any(property => !AllowedLatencyFields.Contains(property.Key)))
        {
            return InvalidRequest();
        }

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

    private static PrivateRuntimeResponse Tts(PrivateRuntimeRequest request)
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

        _ = ReadText(payload, "voice_label", 128);
        var speed = ReadNumber(payload, "speed") ?? 1.0;
        return double.IsFinite(speed) && speed is >= 0.5 and <= 2.0
            ? Unavailable()
            : InvalidRequest();
    }

    private static PrivateRuntimeResponse LifecycleUnavailable(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: true);
        _ = ReadText(payload, "voice_label", 128);
        return Unavailable();
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

    private static PrivateRuntimeResponse Unavailable() => JsonResponse(
        503,
        new JsonObject
        {
            ["status"] = "error",
            ["message"] = "Voice runtime is unavailable.",
        });

    private static PrivateRuntimeResponse InvalidRequest() => JsonResponse(
        422,
        new JsonObject { ["detail"] = "Invalid voice request." });

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) =>
        new(statusCode, "application/json", StrictUtf8.GetBytes(payload.ToJsonString()));
}
