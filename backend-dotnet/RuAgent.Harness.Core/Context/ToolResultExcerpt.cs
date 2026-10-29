using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

/// <summary>Stable model-only excerpts. Original durable receipts and structured cursors stay intact.</summary>
public static class ToolResultExcerpt
{
    public const int ThresholdChars = 8_192;
    private const int HeadChars = 4_096;
    private const int TailChars = 1_024;

    public static string Project(string content)
    {
        if (content.Length <= ThresholdChars || IsStructured(content)) return content;
        var head = HeadChars;
        var tail = content.Length - TailChars;
        if (char.IsHighSurrogate(content[head - 1])) head--;
        if (char.IsLowSurrogate(content[tail])) tail++;
        return content[..head] + "\n[tool result middle omitted; original durable receipt retained; read the source or output cursor for details]\n"
            + ToolDiagnosticExcerpt.Select(content, head, tail) + content[tail..];
    }

    private static bool IsStructured(string content)
    {
        if (content.AsSpan().TrimStart() is not { Length: > 0 } trimmed || trimmed[0] is not ('{' or '[')) return false;
        try { using var document = JsonDocument.Parse(content); return true; }
        catch (JsonException) { return false; }
    }
}
