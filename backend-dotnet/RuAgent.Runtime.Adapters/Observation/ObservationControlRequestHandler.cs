using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Observation;

/// <summary>
/// Keeps the observation control contract explicit while the managed runtime
/// does not yet own a Windows screen-capture/reaction loop. Read/status calls
/// are deterministic; start/react fail closed with a stable response instead
/// of falling back to the removed Python HTTP surface.
/// </summary>
public sealed class ObservationControlRequestHandler : IPrivateRuntimeRequestHandler
{
    private const int MaxRequestBodyBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ManagedRuntimeSettingsService _settings;
    private readonly bool _voiceAvailable;
    private readonly object _gate = new();
    private double _intervalSeconds = 15.0;

    public ObservationControlRequestHandler(
        ManagedRuntimeSettingsService settings,
        bool voiceAvailable)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _voiceAvailable = voiceAvailable;
    }

    public Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PathAndQuery.Split('?', 2)[0];
        return (request.Method, path) switch
        {
            ("POST", "/observation/preflight") => Task.FromResult(Preflight()),
            ("GET", "/observation/vision-config") => Task.FromResult(VisionConfig()),
            ("POST", "/observation/react") => Task.FromResult(React(request)),
            ("POST", "/observation/cancel") => Task.FromResult(Cancel(request)),
            ("POST", "/observation/ack") => Task.FromResult(Acknowledge(request)),
            ("POST", "/observation/start") => Task.FromResult(Start(request)),
            ("POST", "/observation/stop") => Task.FromResult(Stop()),
            ("GET", "/observation/status") => Task.FromResult(Status()),
            _ => throw new PrivateRuntimeRequestException("unsupported_route"),
        };
    }

    private PrivateRuntimeResponse Preflight()
    {
        var backend = _settings.Current.Backend;
        if (backend is not ("OpenCode Zen" or "Kilo Gateway"))
        {
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = "Screen reactions require an active OpenCode or Kilo cloud backend.",
            });
        }

        return JsonResponse(200, new JsonObject
        {
            ["status"] = "error",
            ["message"] = _voiceAvailable
                ? "Screen observation is unavailable in the managed runtime."
                : "Screen observation requires an available voice runtime.",
        });
    }

    private static PrivateRuntimeResponse VisionConfig() => JsonResponse(
        200,
        new JsonObject
        {
            ["status"] = "disabled",
            ["engine"] = "gemini",
            ["message"] = "Gemini Vision API key is not available to the device worker.",
        });

    private static PrivateRuntimeResponse React(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        if (!TryReadReactionPayload(payload))
        {
            return InvalidRequest();
        }

        return JsonResponse(200, new JsonObject
        {
            ["status"] = "skipped",
            ["reason"] = "observation_runtime_unavailable",
        });
    }

    private static PrivateRuntimeResponse Cancel(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        return TryReadObservationId(payload, out _)
            ? JsonResponse(200, new JsonObject { ["status"] = "ok" })
            : InvalidRequest();
    }

    private static PrivateRuntimeResponse Acknowledge(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        if (!TryReadObservationId(payload, out _)
            || payload["played"] is not JsonValue playedValue
            || !playedValue.TryGetValue<bool>(out _))
        {
            return InvalidRequest();
        }

        return JsonResponse(200, new JsonObject { ["status"] = "ok" });
    }

    private PrivateRuntimeResponse Start(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: true);
        if (payload.TryGetPropertyValue("interval_seconds", out var intervalNode)
            && intervalNode is not null
            && (intervalNode is not JsonValue intervalValue
                || !intervalValue.TryGetValue<double>(out var interval)
                || !double.IsFinite(interval)
                || interval is < 0.5 or > 120.0))
        {
            return InvalidRequest();
        }

        if (payload.TryGetPropertyValue("interval_seconds", out intervalNode)
            && intervalNode is JsonValue value
            && value.TryGetValue<double>(out var parsed))
        {
            lock (_gate)
            {
                _intervalSeconds = parsed;
            }
        }

        return JsonResponse(200, new JsonObject
        {
            ["status"] = "error",
            ["message"] = "Screen observation is unavailable in the managed runtime.",
        });
    }

    private static PrivateRuntimeResponse Stop() => JsonResponse(
        200,
        new JsonObject { ["status"] = "ok", ["active"] = false });

    private PrivateRuntimeResponse Status()
    {
        double interval;
        lock (_gate)
        {
            interval = _intervalSeconds;
        }

        return JsonResponse(200, new JsonObject
        {
            ["status"] = "disabled",
            ["active"] = false,
            ["frame_count"] = 0,
            ["vision_count"] = 0,
            ["emitted_count"] = 0,
            ["stale_drops"] = 0,
            ["ms_before"] = 0,
            ["ms_cloud"] = 0,
            ["ms_after"] = 0,
            ["last_motion_delta"] = 0.0,
            ["engine"] = "disabled",
            ["interval_seconds"] = interval,
            ["last_scene"] = null,
            ["last_event"] = null,
            ["last_reaction"] = null,
            ["last_reaction_id"] = null,
            ["last_reaction_at"] = null,
            ["last_error"] = "Screen observation is unavailable in the managed runtime.",
        });
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

    private static bool TryReadObservationId(JsonObject payload, out string id)
    {
        id = string.Empty;
        if (payload["observation_id"] is not JsonValue value
            || !value.TryGetValue<string>(out var parsed)
            || parsed.Length is < 1 or > 64
            || parsed.Any(character => !IsAsciiObservationIdCharacter(character)))
        {
            return false;
        }

        id = parsed;
        return true;
    }

    private static bool TryReadReactionPayload(JsonObject payload)
    {
        if (!TryReadObservationId(payload, out _)
            || payload["event_age_ms"] is not JsonValue ageValue
            || !ageValue.TryGetValue<int>(out var eventAgeMs)
            || eventAgeMs is < 0 or > 60_000
            || payload["observation"] is not JsonObject observation
            || !TryReadBoundedText(observation, "scene", 500)
            || !TryReadBoundedText(observation, "event", 2_000)
            || !TryReadUnitNumber(observation, "confidence")
            || !TryReadUnitNumber(observation, "novelty")
            || !TryReadUnitNumber(observation, "comment_worthiness"))
        {
            return false;
        }

        if (!observation.TryGetPropertyValue("visible_text", out var visibleNode))
        {
            return true;
        }

        if (visibleNode is not JsonArray visible || visible.Count > 16)
        {
            return false;
        }

        foreach (var item in visible)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || text.Length > 200)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadBoundedText(JsonObject payload, string key, int maximum) =>
        payload[key] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.Length <= maximum;

    private static bool TryReadUnitNumber(JsonObject payload, string key) =>
        payload[key] is JsonValue value
            && value.TryGetValue<double>(out var number)
            && double.IsFinite(number)
            && number is >= 0.0 and <= 1.0;

    private static bool IsAsciiObservationIdCharacter(char value) =>
        value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-';

    private static PrivateRuntimeResponse InvalidRequest() => JsonResponse(
        422,
        new JsonObject { ["detail"] = "Invalid runtime request." });

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) =>
        new(statusCode, "application/json", StrictUtf8.GetBytes(payload.ToJsonString()));
}
