using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

using RuAgent.Broker.Skills;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private static HttpClient CreateWebHttpClient() =>
        new(WebConnectionPolicy.CreateHandler(), disposeHandler: true);

    private async ValueTask<ToolExecutionOutcome> ExecuteReadAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        FileReadResult result;
        try
        {
            result = await ResolveTools(context, TargetPathOf(arguments))
                .Read.ExecuteAsync(arguments, context.ImageInputAccepted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileReadImageException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.Hint);
        }

        return ToolExecutionOutcome.Succeeded(
            result.Output,
            ToolVerificationStates.NotRequired,
            result.Image is null ? "Workspace file read." : "Workspace image read.") with { Image = result.Image };
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

    private ValueTask<ToolExecutionOutcome> ExecuteSkillBodyAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var skillBodies = new SkillBodyStore(ToolsFor(context).MutationResolver.CanonicalRoot);
        var name = arguments["name"]?.GetValue<string>()?.Trim();
        var query = arguments["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name) == string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("skill_requires_name_or_query");
        if (!string.IsNullOrWhiteSpace(query))
        {
            if (arguments["expected_revision"] is not null || arguments["expected_body_sha256"] is not null)
                throw new ArgumentException("skill_query_does_not_accept_body_version");
            var catalog = skillBodies.Discover(query, cancellationToken: cancellationToken);
            var items = catalog.Items.Where(item => item.Enabled && item.ModelInvocable).Take(32).Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["description"] = item.Description,
                ["source_path"] = item.SourcePath,
                ["resource_base"] = item.ResourceBase,
                ["revision"] = item.Revision,
                ["enabled"] = item.Enabled,
                ["model_invocable"] = item.ModelInvocable,
                ["user_invocable"] = item.UserInvocable,
                ["requires"] = new JsonArray(item.Requires.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["compatibility"] = item.Compatibility,
            }).ToArray();
            var diagnostics = catalog.Diagnostics.Take(64).Select(item => (JsonNode?)new JsonObject
            {
                ["source_path"] = item.SourcePath,
                ["code"] = item.Code,
            }).ToArray();
            return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
                new JsonObject
                {
                    ["items"] = new JsonArray(items),
                    ["diagnostics"] = new JsonArray(diagnostics),
                    ["truncated"] = catalog.Truncated || catalog.Items.Count > items.Length,
                    ["total"] = catalog.Total,
                }, ToolVerificationStates.NotRequired, "Workspace skill catalog searched."));
        }
        var expectedRevision = arguments["expected_revision"]?.GetValue<string>();
        var expectedBodySha256 = arguments["expected_body_sha256"]?.GetValue<string>();
        if (!skillBodies.TryLoadBody(name!, out var body, out var errorCode, expectedRevision, expectedBodySha256) || body is null)
        {
            throw new UnauthorizedAccessException(errorCode ?? "skill_not_found");
        }

        var unmet = EvaluateSkillRequirements(body.Requires);
        if (unmet is not null)
        {
            return ValueTask.FromResult(unmet);
        }

        return ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["name"] = body.Name,
                ["description"] = body.Description,
                ["body"] = body.Body,
                ["body_sha256"] = body.BodySha256,
                ["id"] = body.Id,
                ["source_path"] = body.SourcePath,
                ["resource_base"] = body.ResourceBase,
                ["revision"] = body.Revision,
                ["requires"] = new JsonArray(body.Requires.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["compatibility"] = body.Compatibility,
                ["model_invocable"] = body.ModelInvocable,
                ["user_invocable"] = body.UserInvocable,
            },
            ToolVerificationStates.NotRequired,
            "Workspace skill loaded."));
    }

    private ToolExecutionOutcome? EvaluateSkillRequirements(IReadOnlyList<string> requires)
    {
        var failure = SkillRequirements.FindUnmet(requires, _skillCapabilityAvailable);
        return failure is null
            ? null
            : ToolExecutionOutcome.Failed(failure.Code, failure.Message, ToolVerificationStates.NotRequired);
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

        var fetched = await _webFetch.FetchAsync(uri, cancellationToken,
            ReadOptionalInt(arguments, "offset") ?? 0,
            ReadOptionalInt(arguments, "limit") ?? WebContentSanitizer.DefaultMaxCharacters).ConfigureAwait(false);
        return ToolExecutionOutcome.Succeeded(
            new JsonObject
            {
                ["untrusted"] = true,
                ["source"] = ToJson(fetched.Source),
                ["content"] = fetched.SanitizedText,
                ["content_type"] = fetched.ContentType,
                ["offset"] = fetched.Offset,
                ["next_offset"] = fetched.NextOffset,
                ["truncated"] = fetched.NextOffset is not null,
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
