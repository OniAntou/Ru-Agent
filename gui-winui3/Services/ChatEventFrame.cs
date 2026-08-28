using System.Text.Json;

namespace RuApp.Services;

internal sealed record ChatEventFrame(
    string Type,
    string? Content,
    string? SessionId,
    string? ErrorCode,
    string? CorrelationId)
{
    public bool IsTerminal => Type is "done" or "error";
}

internal enum ChatTurnUpdateKind
{
    Ignored,
    Session,
    Reset,
    Delta,
    Done,
    Error,
}

internal sealed record ChatTurnUpdate(
    ChatTurnUpdateKind Kind,
    ChatTurn? Turn = null,
    string? Content = null,
    string? SessionId = null,
    string? ErrorCode = null);

internal static class ChatEventFrameParser
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "type",
        "content",
        "session_id",
        "error_code",
        "correlation_id",
    };

    public static bool TryParse(string json, out ChatEventFrame? frame)
    {
        frame = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(typeProperty.GetString()))
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !AllowedProperties.Contains(property.Name))
                {
                    return false;
                }

                if (property.Name is "content" or "session_id" or "error_code" or "correlation_id")
                {
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        return false;
                    }
                }
            }

            frame = new ChatEventFrame(
                typeProperty.GetString()!,
                ReadString(root, "content"),
                ReadString(root, "session_id"),
                ReadString(root, "error_code"),
                ReadString(root, "correlation_id"));
            if (frame.CorrelationId is not null && !IsSafeCorrelationId(frame.CorrelationId))
            {
                frame = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsSafeCorrelationId(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length is < 8 or > 64)
        {
            return false;
        }

        return candidate.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-');
    }
}
