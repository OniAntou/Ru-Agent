using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Devices;

/// <summary>
/// Owns the small HTTP-facing VTube Studio state surface used by the desktop.
/// The websocket client remains the only component that performs VTS I/O;
/// this handler keeps GUI state in memory and maps provider failures to stable
/// responses without exposing transport details.
/// </summary>
public sealed class VtubeStudioRuntimeRequestHandler : IPrivateRuntimeRequestHandler
{
    private const int MaxRequestBodyBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> SafeConnectErrorCodes =
        new HashSet<string>(
            [
                "vts_timeout",
                "vts_unavailable",
                "vts_auth_failed",
                "vts_catalog_unavailable",
                "malformed_response",
            ],
            StringComparer.Ordinal);
    private readonly IVtubeStudioClient _client;
    private readonly object _gate = new();
    private VtsCatalog? _catalog;
    private bool _connected;
    private string _state = "idle";
    private string? _connectError;
    private bool _typing;
    private bool _speaking;
    private bool _mouseLookEnabled;
    private double _mouseLookSensitivity = 1.0;
    private bool _overlayEnabled;
    private string? _overlayError;
    private VtubeStudioMotionConfig _config = VtubeStudioMotionConfig.Defaults;

    public VtubeStudioRuntimeRequestHandler(IVtubeStudioClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
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
            ("GET", "/vts/mouse-look") => MouseLookPayload(),
            ("POST", "/vts/mouse-look") => SaveMouseLook(request),
            ("GET", "/vts/overlay") => OverlayPayload(),
            ("POST", "/vts/overlay") => SaveOverlay(request),
            ("GET", "/vts/status") => JsonResponse(200, StatusPayload()),
            ("POST", "/vts/connect") => await ConnectAsync(cancellationToken).ConfigureAwait(false),
            ("GET", "/vts/config") => JsonResponse(200, ConfigPayload()),
            ("POST", "/vts/config") => SaveConfig(request),
            ("POST", "/vts/activity") => SaveActivity(request),
            ("POST", "/vts/action") => await TriggerActionAsync(request, cancellationToken).ConfigureAwait(false),
            ("POST", "/vts/speaking") => SaveSpeaking(request),
            _ => throw new PrivateRuntimeRequestException("unsupported_route"),
        };
    }

    private PrivateRuntimeResponse MouseLookPayload()
    {
        lock (_gate)
        {
            return new PrivateRuntimeResponse(
                200,
                "application/json",
                StrictUtf8.GetBytes(new JsonObject
                {
                    ["status"] = "ok",
                    ["mouse_look"] = new JsonObject
                    {
                        ["enabled"] = _mouseLookEnabled,
                        ["sensitivity"] = _mouseLookSensitivity,
                    },
                }.ToJsonString()));
        }
    }

    private PrivateRuntimeResponse SaveMouseLook(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var enabled = RequiredBoolean(payload, "enabled");
        var sensitivity = OptionalNumber(payload, "sensitivity") ?? 1.0;
        if (!double.IsFinite(sensitivity) || sensitivity is < 0.1 or > 3.0)
        {
            return InvalidRequest();
        }

        lock (_gate)
        {
            _mouseLookEnabled = enabled;
            _mouseLookSensitivity = sensitivity;
        }

        return JsonResponse(200, new JsonObject { ["status"] = "ok" });
    }

    private PrivateRuntimeResponse OverlayPayload()
    {
        lock (_gate)
        {
            return JsonResponse(
                200,
                new JsonObject
                {
                    ["status"] = "ok",
                    ["overlay"] = new JsonObject
                    {
                        ["enabled"] = _overlayEnabled,
                        // Overlay process ownership is not part of the VTS
                        // websocket adapter yet; fail closed and be explicit.
                        ["running"] = false,
                        ["error"] = _overlayError,
                    },
                });
        }
    }

    private PrivateRuntimeResponse SaveOverlay(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var enabled = RequiredBoolean(payload, "enabled");
        if (enabled)
        {
            // The managed v1 graph does not own the optional overlay process yet.
            // Do not persist a requested-but-inactive flag that would make the
            // desktop believe the overlay is running.
            lock (_gate)
            {
                _overlayEnabled = false;
                _overlayError = "overlay_unavailable";
            }

            return JsonResponse(
                200,
                new JsonObject
                {
                    ["status"] = "error",
                    ["reason"] = "overlay_unavailable",
                    ["enabled"] = false,
                    ["running"] = false,
                });
        }

        lock (_gate)
        {
            _overlayEnabled = enabled;
            _overlayError = null;
        }

        return JsonResponse(
            200,
            new JsonObject
            {
                ["status"] = "ok",
                ["enabled"] = enabled,
                ["running"] = false,
                ["error"] = enabled ? "overlay_unavailable" : null,
            });
    }

    private JsonObject StatusPayload()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["status"] = "ok",
                ["connected"] = _connected,
                ["model_name"] = _catalog?.ModelName ?? string.Empty,
                ["expressions"] = ToJsonArray(_catalog?.ExpressionCodes),
                ["hotkeys"] = ToJsonArray(_catalog?.HotkeyCodes),
                ["state"] = _state,
                ["config"] = _config.ToJson(),
                ["mouse_look_enabled"] = _mouseLookEnabled,
                ["mouse_look_sensitivity"] = _mouseLookSensitivity,
                ["overlay_enabled"] = _overlayEnabled,
                ["overlay_running"] = false,
                ["connect_error"] = _connectError,
            };
        }
    }

    private async Task<PrivateRuntimeResponse> ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_connected)
            {
                return JsonResponse(200, new JsonObject { ["status"] = "ok", ["connected"] = true });
            }
        }

        try
        {
            var catalog = await _client.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _catalog = catalog;
                _connected = true;
                _state = _speaking ? "speaking" : _typing ? "typing" : "idle";
                _connectError = null;
            }

            return JsonResponse(200, new JsonObject { ["status"] = "ok", ["connected"] = true });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VtubeStudioProviderException error)
        {
            return SetConnectFailure(error.Code);
        }
        catch
        {
            return SetConnectFailure("vts_unavailable");
        }
    }

    private PrivateRuntimeResponse SetConnectFailure(string code)
    {
        var safeCode = SafeConnectErrorCodes.Contains(code) ? code : "vts_unavailable";
        lock (_gate)
        {
            _connected = false;
            _catalog = null;
            _connectError = safeCode;
            _state = "idle";
        }

        return JsonResponse(
            200,
            new JsonObject
            {
                ["status"] = "error",
                ["message"] = "VTube Studio is not running or did not authenticate. Open VTube Studio, allow Ru, then retry.",
            });
    }

    private PrivateRuntimeResponse SaveConfig(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        VtubeStudioMotionConfig previous;
        lock (_gate)
        {
            previous = _config;
        }

        try
        {
            var config = VtubeStudioMotionConfig.Parse(payload, previous);
            lock (_gate)
            {
                _config = config;
            }

            return JsonResponse(200, new JsonObject
            {
                ["status"] = "ok",
                ["config"] = config.ToJson(),
            });
        }
        catch (ArgumentException)
        {
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = "Invalid VTube Studio motion configuration.",
            });
        }
    }

    private JsonObject ConfigPayload()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["status"] = "ok",
                ["config"] = _config.ToJson(),
            };
        }
    }

    private PrivateRuntimeResponse SaveActivity(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var typing = RequiredBoolean(payload, "typing");
        lock (_gate)
        {
            _typing = typing;
            if (_connected && !_speaking)
            {
                _state = typing ? "typing" : "idle";
            }
        }

        return JsonResponse(200, new JsonObject { ["status"] = "ok" });
    }

    private async Task<PrivateRuntimeResponse> TriggerActionAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var facial = OptionalText(payload, "facial", 64);
        var body = OptionalText(payload, "body", 64);
        var codesText = OptionalText(payload, "codes", 256);
        if (string.IsNullOrWhiteSpace(codesText))
        {
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = string.IsNullOrWhiteSpace(facial) && string.IsNullOrWhiteSpace(body)
                    ? "provide codes, facial, or body"
                    : "semantic VTube Studio actions are not available in the managed adapter.",
            });
        }

        VtsCatalog catalog;
        lock (_gate)
        {
            if (!_connected || _catalog is null)
            {
                return JsonResponse(200, new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = "VTube Studio is not connected.",
                });
            }

            catalog = _catalog;
        }

        var codes = codesText
            .Replace(',', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<string> normalized;
        try
        {
            normalized = catalog.ValidateActionCodes(codes);
        }
        catch (ArgumentException)
        {
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = "unknown VTube Studio action code.",
            });
        }

        try
        {
            await _client.TriggerAsync(normalized, cancellationToken).ConfigureAwait(false);
            return JsonResponse(200, new JsonObject { ["status"] = "ok" });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VtubeStudioProviderException)
        {
            MarkDisconnected("vts_unavailable");
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = "VTube Studio action failed.",
            });
        }
        catch
        {
            MarkDisconnected("vts_unavailable");
            return JsonResponse(200, new JsonObject
            {
                ["status"] = "error",
                ["message"] = "VTube Studio action failed.",
            });
        }
    }

    private PrivateRuntimeResponse SaveSpeaking(PrivateRuntimeRequest request)
    {
        var payload = ParseObject(request, allowEmpty: false);
        var speaking = RequiredBoolean(payload, "speaking");
        lock (_gate)
        {
            _speaking = speaking;
            if (!_connected)
            {
                return JsonResponse(200, new JsonObject { ["status"] = "ok", ["connected"] = false });
            }

            _state = speaking ? "speaking" : _typing ? "typing" : "idle";
        }

        return JsonResponse(200, new JsonObject { ["status"] = "ok", ["connected"] = true });
    }

    private void MarkDisconnected(string code)
    {
        var safeCode = SafeConnectErrorCodes.Contains(code) ? code : "vts_unavailable";
        lock (_gate)
        {
            _connected = false;
            _catalog = null;
            _connectError = safeCode;
            _state = "idle";
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string>? values) =>
        new((values ?? []).OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => (JsonNode?)value)
            .ToArray());

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

    private static bool RequiredBoolean(JsonObject payload, string key)
    {
        if (payload[key] is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new PrivateRuntimeRequestException("invalid_request");
    }

    private static string? OptionalText(JsonObject payload, string key, int maximum)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.Length <= maximum
            && !text.Any(char.IsControl))
        {
            return text;
        }

        throw new PrivateRuntimeRequestException("invalid_request");
    }

    private static double? OptionalNumber(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        if (node is JsonValue value
            && value.TryGetValue<double>(out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        throw new PrivateRuntimeRequestException("invalid_request");
    }

    private static PrivateRuntimeResponse InvalidRequest() => JsonResponse(
        422,
        new JsonObject { ["detail"] = "Invalid runtime request." });

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) =>
        new(statusCode, "application/json", StrictUtf8.GetBytes(payload.ToJsonString()));

}
