using System.Text.Json.Nodes;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Web;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

/// <summary>
/// Adapts the Broker's bounded web implementation to the in-process Harness gateway.
/// The gateway remains responsible for the evaluate/prepare/execute authorization lifecycle.
/// </summary>
public sealed class AgentWebToolExecutor
{
    private readonly WebOptions _options;
    private readonly WebSearchAdapter? _search;
    private readonly WebFetchAdapter? _fetch;

    public AgentWebToolExecutor(WebOptions options, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!_options.Enabled)
        {
            return;
        }

        var policy = new WebPolicy();
        var sanitizer = new WebContentSanitizer();
        _fetch = new WebFetchAdapter(
            httpClient,
            policy,
            sanitizer,
            _options.MaxResponseBytes);
        if (_options.SearchEndpoint is not null)
        {
            _search = new WebSearchAdapter(
                httpClient,
                policy,
                sanitizer,
                _options.SearchEndpoint,
                _options.MaxResponseBytes,
                _options.FallbackSearchEndpoint);
        }
    }

    public bool IsEnabled => _options.Enabled;

    public bool IsSearchEnabled => _options.SearchEnabled;

    public bool IsFetchEnabled => _options.FetchEnabled;

    public async Task<JsonObject> ExecuteAsync(
        string toolId,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(arguments);

        if (toolId is not ("web_search" or "web_fetch"))
        {
            throw new ArgumentException("web_tool_not_supported", nameof(toolId));
        }

        var definition = AgentModelToolContractCatalog.Find(
            toolId,
            AgentModelToolContractCatalog.CurrentVersion);
        if (definition is null
            || !AgentModelToolContractCatalog.WebToolIds.Contains(toolId))
        {
            throw new ArgumentException("web_tool_not_supported", nameof(toolId));
        }

        JsonObject boundedArguments;
        try
        {
            boundedArguments = arguments.DeepClone().AsObject();
            ToolSchemaValidator.Validate(
                definition.InputSchema,
                boundedArguments,
                "invalid_arguments");
        }
        catch (ToolSchemaException error)
        {
            throw new ArgumentException(error.Code, nameof(arguments), error);
        }

        return toolId switch
        {
            "web_search" => await ExecuteSearchAsync(boundedArguments, cancellationToken).ConfigureAwait(false),
            "web_fetch" => await ExecuteFetchAsync(boundedArguments, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("web_tool_not_supported", nameof(toolId)),
        };
    }

    private async Task<JsonObject> ExecuteSearchAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_search is null || !_options.SearchEnabled)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var citations = await _search.SearchAsync(
            arguments["query"]!.GetValue<string>(),
            ReadMaxResults(arguments),
            cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["untrusted"] = true,
            ["citations"] = new JsonArray(
                citations.Select(ToJson).Cast<JsonNode?>().ToArray()),
        };
    }

    private async Task<JsonObject> ExecuteFetchAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_fetch is null || !_options.FetchEnabled)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var rawUri = arguments["url"]!.GetValue<string>();
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("web_uri_invalid", nameof(arguments));
        }

        var fetched = await _fetch.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["untrusted"] = true,
            ["source"] = ToJson(fetched.Source),
            ["content"] = fetched.SanitizedText,
            ["content_type"] = fetched.ContentType,
        };
    }

    private static JsonObject ToJson(SourceCitation citation) => new()
    {
        ["title"] = citation.Title.Length <= 256 ? citation.Title : citation.Title[..256],
        ["url"] = citation.Url.ToString(),
        ["snippet"] = citation.Snippet is null
            ? null
            : citation.Snippet.Length <= 512 ? citation.Snippet : citation.Snippet[..512],
    };

    private static int ReadMaxResults(JsonObject arguments) =>
        arguments["max_results"] is JsonValue value && value.TryGetValue<int>(out var parsed)
            ? Math.Clamp(parsed, 1, 10)
            : 10;
}
