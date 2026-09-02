using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Help;

public sealed class HelpDocsTool
{
    public const int MaxOutputBytes = 64 * 1024;
    private readonly HelpDocsSearch _search;

    public HelpDocsTool(BundledHelpDocsCatalog catalog)
    {
        _search = new HelpDocsSearch(catalog);
    }

    public static HelpDocsTool CreateDefault() => new(BundledHelpDocsCatalog.LoadDefault());

    public JsonObject Execute(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ToolSchemaValidator.Validate(AgentToolDefinitionCatalog.Find("help.docs", AgentToolDefinitionCatalog.CurrentVersion)!.InputSchema, arguments);
        }
        catch (ToolSchemaException error)
        {
            throw new ArgumentException(error.Code, error);
        }

        var query = arguments["query"]?.GetValue<string>()?.Trim() ?? throw new ArgumentException("invalid_query");
        if (query.EnumerateRunes().Count() is < 1 or > 256)
        {
            throw new ArgumentException("invalid_query");
        }

        var maxResults = arguments["max_results"]?.GetValue<int>() ?? 3;
        if (maxResults is < 1 or > 5)
        {
            throw new ArgumentException("invalid_max_results");
        }

        var results = _search.Search(query, maxResults, out var moreResults);
        var output = new JsonObject
        {
            ["query"] = query,
            ["results"] = new JsonArray(results.Select(result => (JsonNode)new JsonObject
            {
                ["doc_id"] = result.DocumentId,
                ["title"] = result.Title,
                ["section"] = result.Section,
                ["excerpt"] = result.Excerpt,
            }).ToArray()),
            ["truncated"] = moreResults || results.Any(result => result.WasExcerptTruncated),
        };

        while (Encoding.UTF8.GetByteCount(output.ToJsonString()) > MaxOutputBytes && output["results"] is JsonArray resultArray && resultArray.Count > 0)
        {
            resultArray.RemoveAt(resultArray.Count - 1);
            output["truncated"] = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }
}
