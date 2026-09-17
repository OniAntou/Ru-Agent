using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService : IToolInvoker
{
    private static HttpClient CreateWebHttpClient() =>
        new(WebConnectionPolicy.CreateHandler(), disposeHandler: true);

    private ValueTask<ToolExecutionOutcome> ExecuteReadAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            ResolveTools(context, TargetPathOf(arguments)).Read.Execute(arguments),
            ToolVerificationStates.NotRequired,
            "Workspace file read."));
    }

    private ValueTask<ToolExecutionOutcome> ExecuteListAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            ResolveTools(context, TargetPathOf(arguments)).List.Execute(arguments),
            ToolVerificationStates.NotRequired,
            "Workspace entries listed."));
    }

    private ValueTask<ToolExecutionOutcome> ExecuteHelpAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = context;
        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            _helpDocs.Execute(arguments, cancellationToken),
            ToolVerificationStates.NotRequired,
            "Bundled Ru documentation searched."));
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteWebSearchAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_webSearch is null)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var citations = await _webSearch.SearchAsync(
            ReadString(arguments, "query"),
            ReadMaxResults(arguments),
            cancellationToken).ConfigureAwait(false);
        var output = new JsonObject
        {
            ["untrusted"] = true,
            ["citations"] = new JsonArray(citations.Select(ToJson).Cast<JsonNode?>().ToArray()),
        };
        return ToolExecutionOutcome.Succeeded(
            output,
            ToolVerificationStates.NotRequired,
            "Web search completed.");
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteWebFetchAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_webFetch is null)
        {
            throw new UnauthorizedAccessException("web_disabled");
        }

        var rawUri = ReadString(arguments, "url");
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("web_uri_invalid");
        }

        var fetched = await _webFetch.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["untrusted"] = true,
                ["source"] = ToJson(fetched.Source),
                ["content"] = fetched.SanitizedText,
                ["content_type"] = fetched.ContentType,
            },
            ToolVerificationStates.NotRequired,
            "Web page fetched.");
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
        ReadOptionalInt(arguments, "max_results") is { } value
            ? Math.Clamp(value, 1, 10)
            : 10;
}
