using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.LanguageServers;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private async ValueTask<ToolExecutionOutcome> ExecuteCodeIntelligenceAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = context;
        try
        {
            var operation = RequiredText(arguments, "operation");
            var language = RequiredText(arguments, "language");
            var path = RequiredText(arguments, "path");
            var line = OptionalNonNegativeInteger(arguments, "line");
            var character = OptionalNonNegativeInteger(arguments, "character");
            if ((operation is "definition" or "references") && (line is null || character is null))
                return ToolExecutionOutcome.Failed("lsp_arguments_invalid", "definition and references require zero-based line and character positions.");
            // Use the execution context's run-bound root. A path grant must not
            // silently move the server's cwd or broaden its workspace authority.
            var canonicalRoot = ToolsFor(context).CanonicalRoot;
            var server = _languageServers.GetOrAdd(canonicalRoot, root => new LanguageServerHost(root, environment: _languageServerEnvironment));
            var allowProtectedPaths = ActivePolicyProfile(context) is { } profile
                && AgentPolicyProfileCatalog.OpensProtectedPaths(profile);
            var output = await server.ExecuteAsync(operation, language, path, line, character, allowProtectedPaths, cancellationToken).ConfigureAwait(false);
            return ToolExecutionOutcome.Succeeded(output, ToolVerificationStates.NotRequired, "Language server query completed.");
        }
        catch (LspException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.Code);
        }
        catch (IOException)
        {
            return ToolExecutionOutcome.Failed("lsp_file_unavailable", "The workspace document could not be read.");
        }
    }

    private static string RequiredText(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new LspException("lsp_arguments_invalid");

    private static int? OptionalNonNegativeInteger(JsonObject arguments, string name)
    {
        if (!arguments.ContainsKey(name)) return null;
        if (arguments[name] is JsonValue value && value.TryGetValue<int>(out var integer) && integer >= 0) return integer;
        throw new LspException("lsp_arguments_invalid");
    }
}
