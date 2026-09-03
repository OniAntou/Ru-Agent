namespace RuBackend.Business.Activity;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class ActivityPreview
{
    private const int MaxActivityOriginalLength = 50_000;
    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BearerPattern = new(@"Bearer\s+\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyPattern = new(@"(?:sk-|key=)[a-zA-Z0-9_\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ControlMarkerPattern = new(@"\[\[.*?\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PathPattern = new(@"(?:[a-zA-Z]:\\|\b/)[a-zA-Z0-9_\-\\/.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (string Preview, int OriginalLength, bool Redacted) FormatPreview(string? text, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, 0, false);
        }

        var origLen = text.Length;
        maxLength = Math.Clamp(maxLength, 0, MaxActivityOriginalLength);
        var redacted = text.Length > MaxActivityOriginalLength;

        // Keep the privacy regex pipeline bounded even when a malformed or
        // hostile event supplies an enormous content string. Preserve the
        // original length separately for the metadata contract.
        var boundedText = text.Length > MaxActivityOriginalLength
            ? text[..MaxActivityOriginalLength]
            : text;

        // Strip control markers
        var cleaned = ControlMarkerPattern.Replace(boundedText, _ =>
        {
            redacted = true;
            return string.Empty;
        });

        // Redact keys
        cleaned = KeyPattern.Replace(cleaned, _ =>
        {
            redacted = true;
            return "[REDACTED_KEY]";
        });

        // Redact bearer tokens
        cleaned = BearerPattern.Replace(cleaned, _ =>
        {
            redacted = true;
            return "[REDACTED_TOKEN]";
        });

        // Redact URLs
        cleaned = UrlPattern.Replace(cleaned, _ =>
        {
            redacted = true;
            return "[URL]";
        });

        // Redact absolute paths
        cleaned = PathPattern.Replace(cleaned, _ =>
        {
            redacted = true;
            return "[PATH]";
        });

        // Normalize whitespace
        var normalized = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

        string preview;
        if (normalized.Length > maxLength)
        {
            preview = maxLength <= 3
                ? "..."[..maxLength]
                : normalized[..(maxLength - 3)].TrimEnd() + "...";
            redacted = true;
        }
        else
        {
            preview = normalized;
        }

        return (preview, origLen, redacted);
    }

    public static ActivityEvent SanitizeEvent(ActivityEvent evt, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        var ts = !double.IsFinite(evt.Ts) || evt.Ts <= 0
            ? now.ToUnixTimeMilliseconds() / 1000d
            : evt.Ts;
        var iso = string.IsNullOrWhiteSpace(evt.Iso) ? now.ToString("O") : evt.Iso;
        var type = string.IsNullOrWhiteSpace(evt.Type) ? "chat" : evt.Type.Trim().ToLowerInvariant();
        var eventId = !string.IsNullOrWhiteSpace(evt.EventId) ? evt.EventId.Trim() : $"evt_{Guid.NewGuid():N}";

        var sanitizedData = new JsonObject();
        var rawData = evt.Data ?? new JsonObject();

        if (type is "chat" or "agent")
        {
            var role = rawData.TryGetPropertyValue("role", out var r) && r is JsonValue rv
                ? rv.GetValue<string>()
                : "assistant";

            string? rawContent = null;
            if (rawData.TryGetPropertyValue("content", out var c) && c is JsonValue cv)
            {
                rawContent = cv.GetValue<string>();
            }
            else if (rawData.TryGetPropertyValue("preview", out var p) && p is JsonValue pv)
            {
                rawContent = pv.GetValue<string>();
            }

            var (preview, origLen, redacted) = FormatPreview(rawContent);

            origLen = NormalizeOriginalLength(rawData, "original_length", origLen);
            if (rawData.TryGetPropertyValue("redacted", out var red) && red is JsonValue redv && redv.TryGetValue<bool>(out var parsedRed))
            {
                redacted = redacted || parsedRed;
            }

            sanitizedData["role"] = role;
            sanitizedData["preview"] = preview;
            sanitizedData["original_length"] = origLen;
            sanitizedData["redacted"] = redacted;
        }
        else if (type == "emotion")
        {
            var emotion = rawData.TryGetPropertyValue("emotion", out var em) && em is JsonValue emv
                ? emv.GetValue<string>()
                : (rawData.TryGetPropertyValue("name", out var n) && n is JsonValue nv ? nv.GetValue<string>() : "neutral");

            var status = rawData.TryGetPropertyValue("status", out var st) && st is JsonValue stv
                ? stv.GetValue<string>()
                : "succeeded";

            sanitizedData["emotion"] = emotion;
            sanitizedData["status"] = status;
        }
        else if (type == "vts_action")
        {
            var category = rawData.TryGetPropertyValue("category", out var cat) && cat is JsonValue catv
                ? catv.GetValue<string>()
                : (rawData.TryGetPropertyValue("action", out var act) && act is JsonValue actv ? actv.GetValue<string>() : "vts_action");

            var status = rawData.TryGetPropertyValue("status", out var st) && st is JsonValue stv
                ? stv.GetValue<string>()
                : "succeeded";

            sanitizedData["category"] = category;
            sanitizedData["status"] = status;
        }
        else if (type == "observation_reaction")
        {
            string? rawPreview = null;
            if (rawData.TryGetPropertyValue("text", out var textValue) && textValue is JsonValue textJson)
            {
                textJson.TryGetValue<string>(out rawPreview);
            }
            else if (rawData.TryGetPropertyValue("preview", out var previewValue) && previewValue is JsonValue previewJson)
            {
                previewJson.TryGetValue<string>(out rawPreview);
            }

            var (preview, originalLength, redacted) = FormatPreview(rawPreview, 120);
            originalLength = NormalizeOriginalLength(rawData, "original_length", originalLength);
            if (rawData.TryGetPropertyValue("redacted", out var redactedValue) &&
                redactedValue is JsonValue redactedJson &&
                redactedJson.TryGetValue<bool>(out var parsedRedacted))
            {
                redacted = redacted || parsedRedacted;
            }

            sanitizedData["preview"] = preview;
            sanitizedData["original_length"] = originalLength;
            sanitizedData["redacted"] = redacted;

            CopyBoundedString(rawData, sanitizedData, "scene", 100);
            CopyBoundedString(rawData, sanitizedData, "event", 100);
            CopyBoundedString(rawData, sanitizedData, "observation_id", 64);
        }
        else if (type == "observation")
        {
            var status = rawData.TryGetPropertyValue("status", out var st) && st is JsonValue stv
                ? stv.GetValue<string>()
                : "succeeded";

            sanitizedData["status"] = status;

            if (rawData.TryGetPropertyValue("duration_ms", out var dur) && dur is JsonValue durv && durv.TryGetValue<double>(out var durVal))
            {
                sanitizedData["duration_ms"] = Math.Round(durVal, 2);
            }
        }
        else
        {
            // Strictly bounded fallback for unclassified activity types
            if (rawData.TryGetPropertyValue("status", out var st) && st is JsonValue stv && stv.TryGetValue<string>(out var stStr))
            {
                sanitizedData["status"] = stStr;
            }
            if (rawData.TryGetPropertyValue("category", out var cat) && cat is JsonValue catv && catv.TryGetValue<string>(out var catStr))
            {
                sanitizedData["category"] = catStr;
            }
            if (rawData.TryGetPropertyValue("duration_ms", out var dur) && dur is JsonValue durv && durv.TryGetValue<double>(out var durVal))
            {
                sanitizedData["duration_ms"] = Math.Round(durVal, 2);
            }
            if (rawData.TryGetPropertyValue("error_code", out var err) && err is JsonValue errv && errv.TryGetValue<string>(out var errStr))
            {
                sanitizedData["error_code"] = errStr;
            }
        }

        return new ActivityEvent(
            eventId,
            ts,
            iso,
            type,
            evt.SessionId,
            sanitizedData);
    }

    private static void CopyBoundedString(JsonObject source, JsonObject target, string key, int maxLength)
    {
        if (source.TryGetPropertyValue(key, out var value) && value is JsonValue json && json.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.Trim();
            target[key] = trimmed[..Math.Min(maxLength, trimmed.Length)];
        }
    }

    private static int NormalizeOriginalLength(JsonObject data, string key, int computedLength)
    {
        var fallback = Math.Clamp(computedLength, 0, MaxActivityOriginalLength);
        if (!data.TryGetPropertyValue(key, out var value) ||
            value is not JsonValue json ||
            !json.TryGetValue<int>(out var parsed) ||
            parsed < 0 ||
            parsed > MaxActivityOriginalLength)
        {
            return fallback;
        }

        return parsed;
    }
}
