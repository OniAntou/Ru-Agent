using System.Text.Json;

namespace RuAgent.Client;

/// <summary>
/// Recognizes model tool-call envelopes that a client must keep out of chat prose.
/// This display-only check does not parse or authorize a tool call.
/// </summary>
public static class AgentActivityDisplayPolicy
{
    private const string ToolCallOpenTag = "<tool_call>";
    private const string ToolCallCloseTag = "</tool_call>";

    public static bool LooksLikeToolSelection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = StripLeadingThinking(text.Trim());
        if (candidate.StartsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var openIndex = candidate.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
        var repaired = RepairDelimiters(candidate, openIndex >= 0 ? openIndex + ToolCallOpenTag.Length : 0);
        if (openIndex >= 0)
        {
            var jsonStart = repaired.IndexOf('{', openIndex + ToolCallOpenTag.Length);
            return jsonStart >= 0
                && TryReadJsonObject(repaired, jsonStart, out var json)
                && HasToolKeys(json);
        }

        // A repaired leading "<(" is now " {": trim before the shape check.
        candidate = StripJsonFence(repaired.TrimStart());
        return candidate.StartsWith('{') && HasToolKeys(candidate);
    }

    private static string StripLeadingThinking(string candidate)
    {
        while (candidate.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var end = candidate.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            candidate = candidate[(end + "</think>".Length)..].Trim();
        }

        return candidate;
    }

    /// <summary>
    /// Same-length repair of "&lt;(" and "}&gt;" at structural positions from
    /// <paramref name="startIndex"/>; JSON string content is left as written,
    /// matching the Harness parser.
    /// </summary>
    private static string RepairDelimiters(string candidate, int startIndex)
    {
        if ((uint)startIndex >= (uint)candidate.Length)
        {
            return candidate;
        }

        char[]? repaired = null;
        var inString = false;
        var escaped = false;
        for (var index = startIndex; index < candidate.Length; index++)
        {
            var current = candidate[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            var next = index + 1 < candidate.Length ? candidate[index + 1] : '\0';
            if (current == '"')
            {
                inString = true;
            }
            else if (current == '<' && next == '(')
            {
                repaired ??= candidate.ToCharArray();
                repaired[index] = ' ';
                repaired[index + 1] = '{';
                index++;
            }
            else if (current == '}' && next == '>')
            {
                repaired ??= candidate.ToCharArray();
                repaired[index + 1] = ' ';
                index++;
            }
        }

        return repaired is null ? candidate : new string(repaired);
    }

    private static string StripJsonFence(string candidate)
    {
        if (!candidate.StartsWith("```", StringComparison.Ordinal))
        {
            return candidate;
        }

        var firstNewline = candidate.IndexOf('\n');
        if (firstNewline < 0)
        {
            return candidate;
        }

        var header = candidate[..firstNewline].Trim();
        var lastFence = candidate.LastIndexOf("```", StringComparison.Ordinal);
        if (header is not ("```" or "```json" or "```JSON") || lastFence <= firstNewline)
        {
            return candidate;
        }

        return candidate[(firstNewline + 1)..lastFence].Trim();
    }

    private static bool TryReadJsonObject(string text, int start, out string json)
    {
        json = string.Empty;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                json = text[start..(index + 1)];
                return true;
            }
        }

        return false;
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

            var hasId = false;
            var hasVersion = false;
            var hasArguments = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                hasId |= property.NameEquals("tool_id");
                hasVersion |= property.NameEquals("tool_version");
                hasArguments |= property.NameEquals("arguments");
            }

            return hasId && hasVersion && hasArguments;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
