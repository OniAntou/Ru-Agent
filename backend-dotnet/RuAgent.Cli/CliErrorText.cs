using System.Text.Json;
using RuAgent.Client;

namespace RuAgent.Cli;

/// <summary>One-line human error text: code plus the bounded server message, never raw JSON.</summary>
public static class CliErrorText
{
    public const int MaxMessageChars = 200;

    public static string Format(AgentApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return $"{ex.ErrorCode}: {ShortMessage(ex.Message)}";
    }

    public static string ShortMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "request failed";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(msg.GetString()))
            {
                return SingleLine(msg.GetString()!, MaxMessageChars);
            }
        }
        catch (JsonException)
        {
        }

        return SingleLine(body, MaxMessageChars);
    }

    private static string SingleLine(string text, int maximum)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= maximum ? flat : Tui.TuiText.SafeCut(flat, maximum - 1) + "…";
    }
}
