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
