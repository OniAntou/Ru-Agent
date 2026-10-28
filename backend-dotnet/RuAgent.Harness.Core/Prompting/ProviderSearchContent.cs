using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// The content a provider-hosted web search adds to a model response (ADR 0033), and
/// the rules for keeping or dropping it when history is replayed.
/// </summary>
/// <remarks>
/// Anthropic rejects a replayed search result whose <c>encrypted_content</c> or citation
/// <c>encrypted_index</c> was changed, so kept blocks travel unchanged, including fields
/// Ru does not know yet. Validation therefore checks the required fields and bounds the
/// rest by size instead of by an exact key set.
/// </remarks>
public static class ProviderSearchContent
{
    public const string AnthropicToolName = "web_search";
    public const int MaximumBlockBytes = 96 * 1024;
    public const int MaximumCitations = 64;
    private const int MaximumIdentityChars = 256;
    private const int MaximumUrlChars = 4096;
    private const int MaximumResults = 64;

    public static bool IsAnthropicSearchBlock(JsonObject block) =>
        Type(block) is "server_tool_use" or "web_search_tool_result";

    /// <summary>Whether an Anthropic block is search content Ru accepts in a response or history.</summary>
    public static bool IsValidAnthropicSearchBlock(JsonObject block) =>
        Bounded(block) && Type(block) switch
        {
            "server_tool_use" => IsIdentity(block["id"])
                && String(block["name"]) == AnthropicToolName
                && block["input"] is JsonObject input && input.All(property => property.Value is null or JsonValue),
            "web_search_tool_result" => IsIdentity(block["tool_use_id"]) && IsSearchResultContent(block["content"]),
            _ => false,
        };

    /// <summary>Whether a text block's <c>citations</c> field holds web search citations.</summary>
    public static bool IsValidCitations(JsonNode? citations) =>
        citations is JsonArray list && list.Count <= MaximumCitations
        && list.All(node => node is JsonObject citation
            && Type(citation) == "web_search_result_location"
            && IsUrl(citation["url"])
            && citation["encrypted_index"] is JsonValue index && index.TryGetValue<string>(out var value) && value.Length > 0);

    /// <summary>Whether a Responses output item is a hosted web search call.</summary>
    public static bool IsValidResponsesSearchCall(JsonObject item) =>
        Type(item) == "web_search_call" && IsIdentity(item["id"]) && Bounded(item)
        && (item["action"] is null or JsonObject);

    public static bool IsResponsesSearchCall(JsonObject item) => Type(item) == "web_search_call";

    /// <summary>
    /// Keeps a block list replayable: a search call whose result is not in the same list
    /// is dropped. Anthropic defers such a call when it is mixed with client tool calls
    /// and would run it on the next request; dropping it means that search does not run.
    /// </summary>
    public static IReadOnlyList<JsonObject> DropUnansweredSearchCalls(IReadOnlyList<JsonObject> blocks)
    {
        var answered = blocks.Where(block => Type(block) == "web_search_tool_result")
            .Select(block => String(block["tool_use_id"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var called = blocks.Where(block => Type(block) == "server_tool_use")
            .Select(block => String(block["id"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return blocks.Where(block => Type(block) switch
        {
            "server_tool_use" => answered.Contains(String(block["id"]) ?? string.Empty),
            "web_search_tool_result" => called.Contains(String(block["tool_use_id"]) ?? string.Empty),
            _ => true,
        }).ToArray();
    }

    /// <summary>
    /// Removes every trace of a provider search from replayed blocks: the call, the
    /// result and the citations that point into it. Used for a request that does not
    /// declare provider search, and when a continuation would exceed its bounds.
    /// </summary>
    public static IReadOnlyList<JsonObject> StripAnthropic(IEnumerable<JsonObject> blocks)
    {
        var result = new List<JsonObject>();
        foreach (var block in blocks)
        {
            if (IsAnthropicSearchBlock(block)) continue;
            if (Type(block) == "text" && block.ContainsKey("citations"))
            {
                var copy = (JsonObject)block.DeepClone();
                copy.Remove("citations");
                result.Add(copy);
                continue;
            }
            result.Add(block);
        }
        return result;
    }

    /// <summary>
    /// Removes hosted search calls from replayed Responses items. Their reasoning items go
    /// too: a reasoning item replayed without the item that followed it is rejected, and
    /// reasoning is optional input.
    /// </summary>
    public static IReadOnlyList<JsonObject> StripResponses(IReadOnlyList<JsonObject> items) =>
        items.Any(IsResponsesSearchCall)
            ? items.Where(item => !IsResponsesSearchCall(item) && Type(item) != "reasoning").ToArray()
            : items;

    /// <summary>Distinct cited sources in first-cited order, for the visible Sources list.</summary>
    public static IReadOnlyList<(string Url, string Title)> CitedSources(IEnumerable<JsonObject> citations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<(string, string)>();
        foreach (var citation in citations)
        {
            if (String(citation["url"]) is not { } url || !IsUrl(citation["url"]) || !seen.Add(url)) continue;
            var title = String(citation["title"]);
            sources.Add((url, string.IsNullOrWhiteSpace(title) ? url : title.Trim()));
        }
        return sources;
    }

    /// <summary>The Markdown appended after an answer that cites web sources.</summary>
    public static string FormatSources(IReadOnlyList<(string Url, string Title)> sources)
    {
        if (sources.Count == 0) return string.Empty;
        var text = new StringBuilder("\n\nSources:");
        foreach (var (url, title) in sources.Take(MaximumCitations))
            text.Append("\n- [").Append(EscapeLinkText(title)).Append("](").Append(url.Replace(")", "%29", StringComparison.Ordinal)).Append(')');
        return text.ToString();
    }

    private static string EscapeLinkText(string title)
    {
        var flat = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        flat = flat.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal);
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private static bool IsSearchResultContent(JsonNode? content) => content switch
    {
        JsonArray results => results.Count <= MaximumResults && results.All(node => node is JsonObject result
            && Type(result) == "web_search_result" && IsUrl(result["url"])
            && result["encrypted_content"] is JsonValue encrypted && encrypted.TryGetValue<string>(out _)),
        JsonObject error => Type(error) == "web_search_tool_result_error" && String(error["error_code"]) is { Length: > 0 and <= 64 },
        _ => false,
    };

    private static bool Bounded(JsonObject block) =>
        Encoding.UTF8.GetByteCount(block.ToJsonString()) <= MaximumBlockBytes && !block.ToJsonString().Contains("\\u0000", StringComparison.Ordinal);

    private static bool IsIdentity(JsonNode? node) => String(node) is { Length: > 0 and <= MaximumIdentityChars } value && !value.Contains('\0');

    private static bool IsUrl(JsonNode? node) =>
        String(node) is { Length: > 0 and <= MaximumUrlChars } value
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string? Type(JsonObject block) => String(block["type"]);

    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
