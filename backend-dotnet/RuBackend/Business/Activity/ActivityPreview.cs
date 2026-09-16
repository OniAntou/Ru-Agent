namespace RuBackend.Business.Activity;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class ActivityPreview
{
    private const int MaxActivityOriginalLength = 50_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex BearerPattern = new(@"Bearer\s+\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex KeyPattern = new(@"(?:sk-|key=)[a-zA-Z0-9_\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex ControlMarkerPattern = new(@"\[\[.*?\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex PathPattern = new(@"(?:[a-zA-Z]:\\|\b/)[a-zA-Z0-9_\-\\/.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// Builds a bounded preview. <c>PrivacyRedacted</c> is true only when a
    /// secret, token, URL, path or control marker was actually scrubbed;
    /// <c>Truncated</c> is true when the text was cut for length. The two are
    /// tracked separately so readers can tell "shortened" apart from "scrubbed".
    /// </summary>
    public static (string Preview, int OriginalLength, bool PrivacyRedacted, bool Truncated) FormatPreview(string? text, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, 0, false, false);
        }

        var origLen = text.Length;
        maxLength = Math.Clamp(maxLength, 0, MaxActivityOriginalLength);
        var privacyRedacted = false;
        var truncated = text.Length > MaxActivityOriginalLength;

        // Keep the privacy regex pipeline bounded even when a malformed or
        // hostile event supplies an enormous content string. Preserve the
        // original length separately for the metadata contract.
        var boundedText = text.Length > MaxActivityOriginalLength
            ? text[..MaxActivityOriginalLength]
            : text;

        string cleaned;
        try
        {
            // Strip control markers
            cleaned = ControlMarkerPattern.Replace(boundedText, _ =>
            {
                privacyRedacted = true;
                return string.Empty;
            });

            // Redact keys
            cleaned = KeyPattern.Replace(cleaned, _ =>
            {
                privacyRedacted = true;
                return "[REDACTED_KEY]";
            });

            // Redact bearer tokens
            cleaned = BearerPattern.Replace(cleaned, _ =>
            {
                privacyRedacted = true;
                return "[REDACTED_TOKEN]";
            });

            // Redact URLs
            cleaned = UrlPattern.Replace(cleaned, _ =>
            {
                privacyRedacted = true;
                return "[URL]";
            });

            // Redact absolute paths
            cleaned = PathPattern.Replace(cleaned, _ =>
            {
                privacyRedacted = true;
                return "[PATH]";
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // Hostile input that blows the redaction budget must not stall the
            // pipeline. Keep the length-bounded text and flag it as redacted.
            cleaned = boundedText;
            privacyRedacted = true;
        }

        // Normalize whitespace
        var normalized = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

        string preview;
        if (normalized.Length > maxLength)
        {
            preview = maxLength <= 3
                ? "..."[..maxLength]
                : normalized[..(maxLength - 3)].TrimEnd() + "...";
            truncated = true;
        }
        else
        {
            preview = normalized;
        }

        return (preview, origLen, privacyRedacted, truncated);
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
            var role = ReadBoundedString(rawData, "role", "assistant", 32);

            string? rawContent = ReadString(rawData, "content") ?? ReadString(rawData, "preview");

            var (preview, origLen, privacyRedacted, truncated) = FormatPreview(rawContent);

            origLen = NormalizeOriginalLength(rawData, "original_length", origLen);
            if (ReadOptionalBool(rawData, "redacted"))
            {
                // Legacy files merged truncation into this flag: stay fail-closed.
                privacyRedacted = true;
            }

            sanitizedData["role"] = role;
            sanitizedData["preview"] = preview;
            sanitizedData["original_length"] = origLen;
            sanitizedData["redacted"] = privacyRedacted;
            sanitizedData["truncated"] = truncated;
        }
        else if (type == "emotion")
        {
            var emotion = ReadBoundedString(
                rawData, "emotion",
                ReadBoundedString(rawData, "name", "neutral", 64), 64);

            var status = ReadBoundedString(rawData, "status", "succeeded", 64);

            sanitizedData["emotion"] = emotion;
            sanitizedData["status"] = status;
        }
        else if (type == "vts_action")
        {
            var category = ReadBoundedString(
                rawData, "category",
                ReadBoundedString(rawData, "action", "vts_action", 100), 100);

            var status = ReadBoundedString(rawData, "status", "succeeded", 64);

            sanitizedData["category"] = category;
            sanitizedData["status"] = status;
        }
        else if (type == "observation_reaction")
        {
            string? rawPreview = ReadString(rawData, "text") ?? ReadString(rawData, "preview");

            var (preview, originalLength, privacyRedacted, truncated) = FormatPreview(rawPreview, 120);
            originalLength = NormalizeOriginalLength(rawData, "original_length", originalLength);
            if (ReadOptionalBool(rawData, "redacted"))
            {
                privacyRedacted = true;
            }

            sanitizedData["preview"] = preview;
            sanitizedData["original_length"] = originalLength;
            sanitizedData["redacted"] = privacyRedacted;
            sanitizedData["truncated"] = truncated;

            CopyBoundedString(rawData, sanitizedData, "scene", 100);
            CopyBoundedString(rawData, sanitizedData, "event", 100);
            CopyBoundedString(rawData, sanitizedData, "observation_id", 64);
        }
        else if (type == "observation")
        {
            var status = ReadBoundedString(rawData, "status", "succeeded", 64);

            sanitizedData["status"] = status;

            if (rawData.TryGetPropertyValue("duration_ms", out var dur) && dur is JsonValue durv && durv.TryGetValue<double>(out var durVal))
            {
                sanitizedData["duration_ms"] = Math.Round(durVal, 2);
            }
        }
        else
        {
            // Strictly bounded fallback for unclassified activity types
            CopyBoundedString(rawData, sanitizedData, "status", 64);
            CopyBoundedString(rawData, sanitizedData, "category", 100);
            if (rawData.TryGetPropertyValue("duration_ms", out var dur) && dur is JsonValue durv && durv.TryGetValue<double>(out var durVal))
            {
                sanitizedData["duration_ms"] = Math.Round(durVal, 2);
            }
            CopyBoundedString(rawData, sanitizedData, "error_code", 100);
        }

        return new ActivityEvent(
            eventId,
            ts,
            iso,
            type,
            SanitizeSessionId(evt.SessionId),
            sanitizedData);
    }

    private static string? ReadString(JsonObject source, string key)
    {
        if (source.TryGetPropertyValue(key, out var value)
            && value is JsonValue json
            && json.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    private static string ReadBoundedString(JsonObject source, string key, string fallback, int maxLength)
    {
        var text = ReadString(source, key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var trimmed = text.Trim();
        return trimmed[..Math.Min(maxLength, trimmed.Length)];
    }

    private static string? SanitizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var trimmed = sessionId.Trim();
        return trimmed[..Math.Min(128, trimmed.Length)];
    }

    private static bool ReadOptionalBool(JsonObject source, string key)
    {
        if (source.TryGetPropertyValue(key, out var value)
            && value is JsonValue json
            && json.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return false;
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
