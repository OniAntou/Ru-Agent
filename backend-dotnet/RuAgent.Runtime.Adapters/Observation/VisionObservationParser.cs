using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Observation;

public static partial class VisionObservationParser
{
    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?:api[ _-]?key|password|mật khẩu|token|secret|bearer|authorization|credential|passphrase|access[ _-]?key)\b\s*(?::|=)?\s*(?:là\s+)?[""'“”]*(?<value>\S{3,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"\b(?:\d[ -]*){13,19}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CreditCardPattern();

    [GeneratedRegex(
        @"\b(?:api[ _-]?key|password|mật khẩu|token|secret|bearer|authorization|credential|passphrase|access[ _-]?key|số tài khoản|bệnh án)\b|(?:sk|pk|ghp|gho|github_pat)_[A-Za-z0-9_-]+|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|https?://[^\s<>]+|\[\[(?:VTS|EMO|VOICE_PACE):[^\]]*\]\]|\[(?:SEARCH|APP_OPEN|VOLUME):[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePattern();

    public static VisionObservation Parse(JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var raw = ReadCandidateText(response);
        raw = StripFence(raw);
        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(raw) as JsonObject
                ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new ObservationProviderException("malformed_payload", retryable: false, error);
        }

        var scene = Limit(Redact(ReadText(parsed, "scene") ?? "Desktop"), 200);
        var @event = Limit(Redact(ReadText(parsed, "event") ?? "Working on PC"), 1_500);
        var reaction = ReadText(parsed, "reaction");
        reaction = reaction is null ? null : Limit(Redact(reaction), 500);
        var visible = ReadVisibleText(parsed["visible_text"]);
        return new VisionObservation(
            scene,
            @event,
            ReadNumber(parsed, "confidence", 0.8),
            ReadNumber(parsed, "novelty", 0.5),
            ReadNumber(parsed, "comment_worthiness", 0.5),
            visible,
            reaction);
    }

    public static string Redact(string text)
    {
        var redacted = EmailPattern().Replace(text, "[EMAIL]");
        redacted = SecretPattern().Replace(redacted, "[REDACTED]");
        redacted = SensitivePattern().Replace(redacted, "[REDACTED]");
        redacted = CreditCardPattern().Replace(redacted, "[NUMBER]");
        return redacted;
    }

    private static string Limit(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum];

    private static string ReadCandidateText(JsonObject response)
    {
        if (response["candidates"] is not JsonArray candidates || candidates.Count == 0
            || candidates[0] is not JsonObject candidate
            || candidate["content"] is not JsonObject content
            || content["parts"] is not JsonArray parts || parts.Count == 0
            || parts[0] is not JsonObject part
            || part["text"] is not JsonValue textValue
            || !textValue.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw new ObservationProviderException("malformed_payload", retryable: false);
        }

        return text.Trim();
    }

    private static string StripFence(string text)
    {
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }
        else if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text[3..];
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    private static string? ReadText(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static double ReadNumber(JsonObject obj, string key, double fallback)
    {
        if (obj[key] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<double>(out var number) && double.IsFinite(number))
        {
            return Math.Clamp(number, 0.0, 1.0);
        }

        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number))
        {
            return Math.Clamp(number, 0.0, 1.0);
        }

        return fallback;
    }

    private static IReadOnlyList<string> ReadVisibleText(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in array.Take(16))
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                values.Add(Limit(Redact(text), 150));
            }
            else if (item is JsonValue number
                && number.TryGetValue<double>(out var numeric)
                && double.IsFinite(numeric))
            {
                var numericText = numeric.ToString(CultureInfo.InvariantCulture);
                values.Add(numericText[..Math.Min(150, numericText.Length)]);
            }

            if (values.Count == RuntimeLimits.MaxVisibleTextItems)
            {
                break;
            }
        }

        return values;
    }
}
