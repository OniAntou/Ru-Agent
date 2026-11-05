using System.Text.Json;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// Recognizes a tool call the model wrote out as text (a JSON envelope or a
/// <c>&lt;tool_call&gt;</c> block) instead of calling the tool natively. Native tool
/// calling is the only protocol, so this markup is never executed; the shape checks
/// here keep it out of the chat and let the model step reject it.
/// </summary>
public static class ModelToolSelectionParser
{
    private const string ToolCallOpenTag = "<tool_call>";
    private const string ToolCallCloseTag = "</tool_call>";

    /// <summary>
    /// Splits a trailing tool-call block (prose first, wrapper last) so a late
    /// wrapper is never rendered as chat text. Returns false when there is no
    /// trailing block: no tag at all, an unterminated tag, or prose after the
    /// closing tag. Mid-text syntax discussion is left untouched.
    /// </summary>
    public static bool TrySplitTrailingBlock(string text, out string prose, out string block)
    {
        prose = text;
        block = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var index = text.LastIndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var candidate = text[index..];
        if (!candidate.TrimEnd().EndsWith(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        prose = text[..index].TrimEnd();
        block = candidate;
        return true;
    }

    /// <summary>
    /// Same-length repair of mangled envelope delimiters weak models invent
    /// ("&lt;(" for "{", "}&gt;" for "}"). Length preservation keeps repaired
    /// coordinates valid against the original text.
    /// </summary>
    public static string RepairDelimiters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text
            .Replace("<(", "{", StringComparison.Ordinal)
            .Replace("}>", "}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the first balanced JSON object at or after <paramref name="startIndex"/>,
    /// honouring string literals and escapes. Returns false when unbalanced.
    /// </summary>
    public static bool TryFindFirstJsonObject(string text, int startIndex, out string json, out int start)
    {
        json = string.Empty;
        start = -1;
        if (string.IsNullOrEmpty(text) || (uint)startIndex >= (uint)text.Length)
        {
            return false;
        }

        int depth = 0;
        bool inString = false;
        bool escape = false;
        int objectStart = -1;
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                if (depth == 0)
                {
                    objectStart = i;
                }

                depth++;
            }
            else if (c == '}' && depth > 0 && --depth == 0)
            {
                json = text.Substring(objectStart, i - objectStart + 1);
                start = objectStart;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Presentation shape check shared by the model step and renderers: does this
    /// text carry a tool-selection envelope (any dialect) that must never be
    /// shown as chat? No advertised-capability knowledge required.
    /// </summary>
    public static bool LooksLikeToolSelection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var openIndex = trimmed.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
        if (openIndex >= 0)
        {
            var repaired = RepairDelimiters(trimmed);
            return TryFindFirstJsonObject(repaired, openIndex + ToolCallOpenTag.Length, out var found, out _)
                && HasToolKeys(found);
        }

        var candidate = NormalizeCandidate(RepairDelimiters(trimmed));
        return candidate.StartsWith("{", StringComparison.Ordinal) && HasToolKeys(candidate);
    }

    private static bool HasToolKeys(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool hasId = false;
            bool hasVersion = false;
            bool hasArguments = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("tool_id"))
                {
                    hasId = true;
                }
                else if (property.NameEquals("tool_version"))
                {
                    hasVersion = true;
                }
                else if (property.NameEquals("arguments"))
                {
                    hasArguments = true;
                }
            }

            return hasId && hasVersion && hasArguments;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string NormalizeCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var candidate = text.Trim();

        while (candidate.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var endThink = candidate.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (endThink < 0)
            {
                break;
            }

            candidate = candidate.Substring(endThink + "</think>".Length).Trim();
        }

        if (candidate.StartsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase)
            && candidate.EndsWith(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase))
        {
            var inner = candidate.Substring(
                    ToolCallOpenTag.Length,
                    candidate.Length - ToolCallOpenTag.Length - ToolCallCloseTag.Length)
                .Trim();
            if (inner.StartsWith('{')
                && !inner.Contains(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase)
                && !inner.Contains(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase))
            {
                candidate = inner;
            }
        }
        else if (candidate.StartsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            var body = candidate[ToolCallOpenTag.Length..].Trim();
            if (body.StartsWith('{')
                && !body.Contains(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase)
                && !body.Contains(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase)
                && IsCompleteJsonObject(body))
            {
                candidate = body;
            }
        }

        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = candidate.IndexOf('\n');
            if (firstNewline >= 0)
            {
                var fenceHeader = candidate[..firstNewline].Trim();
                if (fenceHeader is "```" or "```json" or "```JSON")
                {
                    var lastFence = candidate.LastIndexOf("```", StringComparison.Ordinal);
                    if (lastFence > firstNewline)
                    {
                        var inner = candidate.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
                        if (inner.Length > 0 && inner[0] is '{' or '[')
                        {
                            candidate = inner;
                        }
                    }
                }
            }
        }

        return candidate;
    }

    private static bool IsCompleteJsonObject(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
