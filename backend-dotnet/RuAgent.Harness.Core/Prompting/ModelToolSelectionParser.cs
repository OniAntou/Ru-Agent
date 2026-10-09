using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuAgent.Harness.Core.Prompting;

public sealed record ModelToolSelection(string ToolId, string ToolVersion, JsonElement Arguments);

public sealed class ModelToolSelectionException : Exception
{
    public ModelToolSelectionException(string code)
        : base($"tool_selection_{code}")
    {
        Code = code;
    }

    public string Code { get; }
}

public static class ModelToolSelectionParser
{
    private const string ToolCallOpenTag = "<tool_call>";
    private const string ToolCallCloseTag = "</tool_call>";
    private static readonly Regex SemVer = new("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    /// <summary>
    /// Selection extraction for opener-led attempts anywhere in the text (prose
    /// glued to an envelope, possibly with mangled delimiters). Succeeds only
    /// when a tool-keyed JSON object follows the opener; <paramref name="prose"/>
    /// is the text before it with dangling opener/fence remnants removed.
    /// </summary>
    public static bool TryExtractCallCandidate(string text, out string prose, out string envelopeJson)
    {
        prose = text;
        envelopeJson = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var openIndex = text.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
        if (openIndex < 0)
        {
            return false;
        }

        var repaired = RepairDelimiters(text);
        if (!TryFindFirstJsonObject(repaired, openIndex + ToolCallOpenTag.Length, out var found, out var foundAt)
            || !HasToolKeys(found))
        {
            return false;
        }

        var rawProse = repaired[..foundAt].TrimEnd();
        while (true)
        {
            var next = rawProse;
            if (next.EndsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase) && next.Length >= ToolCallOpenTag.Length)
            {
                next = next[..^ToolCallOpenTag.Length].TrimEnd();
            }
            else if (next.EndsWith("```", StringComparison.Ordinal))
            {
                next = next[..next.LastIndexOf("```", StringComparison.Ordinal)].TrimEnd();
            }
            else
            {
                break;
            }

            if (next.Length == rawProse.Length)
            {
                break;
            }

            rawProse = next;
        }

        prose = rawProse;
        envelopeJson = found;
        return true;
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

    public static ModelToolSelection Parse(string text, IReadOnlySet<string> advertisedToolIds)
    {
        if (string.IsNullOrWhiteSpace(text) || advertisedToolIds is null)
        {
            throw new ModelToolSelectionException("invalid_json");
        }

        var candidate = NormalizeCandidate(text);
        if (candidate.Length == 0)
        {
            throw new ModelToolSelectionException("invalid_json");
        }

        byte[] utf8;
        try
        {
            utf8 = Encoding.UTF8.GetBytes(candidate);
        }
        catch (EncoderFallbackException)
        {
            throw new ModelToolSelectionException("invalid_json");
        }

        if (utf8.Length > 1_000_000)
        {
            throw new ModelToolSelectionException("input_too_large");
        }

        try
        {
            EnsureNoDuplicateKeys(utf8);
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ModelToolSelectionException("invalid_json");
            }

            var keys = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            if (!keys.SetEquals(["tool_id", "tool_version", "arguments"]))
            {
                throw new ModelToolSelectionException("invalid_fields");
            }

            var toolId = root.GetProperty("tool_id");
            var version = root.GetProperty("tool_version");
            var arguments = root.GetProperty("arguments");
            if (toolId.ValueKind != JsonValueKind.String || !advertisedToolIds.Contains(toolId.GetString()!))
            {
                throw new ModelToolSelectionException("unknown_tool");
            }

            var versionText = version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            if (versionText is null || versionText.Length > 32 || !SemVer.IsMatch(versionText))
            {
                throw new ModelToolSelectionException("invalid_tool_version");
            }

            if (arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ModelToolSelectionException("invalid_arguments");
            }

            if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 64 * 1024)
            {
                throw new ModelToolSelectionException("arguments_too_large");
            }

            return new ModelToolSelection(toolId.GetString()!, versionText, arguments.Clone());
        }
        catch (ModelToolSelectionException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ModelToolSelectionException("invalid_json");
        }
    }

    private static void EnsureNoDuplicateKeys(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
            if (!reader.Read())
            {
                throw new ModelToolSelectionException("invalid_json");
            }

            ReadValue(ref reader);
            if (reader.Read())
            {
                throw new ModelToolSelectionException("invalid_json");
            }
        }
        catch (ModelToolSelectionException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ModelToolSelectionException("invalid_json");
        }
    }

    private static void ReadValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !keys.Add(reader.GetString()!))
                {
                    throw new ModelToolSelectionException("duplicate_key");
                }

                if (!reader.Read())
                {
                    throw new ModelToolSelectionException("invalid_json");
                }

                ReadValue(ref reader);
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new ModelToolSelectionException("invalid_json");
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                ReadValue(ref reader);
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new ModelToolSelectionException("invalid_json");
            }
        }
        else if (reader.TokenType is JsonTokenType.None or JsonTokenType.PropertyName)
        {
            throw new ModelToolSelectionException("invalid_json");
        }
    }
}
