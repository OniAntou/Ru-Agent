using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Model;

public sealed partial class ModelStepService
{
    private static string? ExtractReasoningText(StringBuilder reasoning, StringBuilder text)
    {
        if (reasoning.Length > 0)
        {
            var excerpt = reasoning.ToString().Trim();
            return excerpt.Length == 0 ? null : excerpt;
        }

        return ExtractThinkBlocks(text.ToString());
    }

    private static string? ExtractThinkBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var collected = new StringBuilder();
        int searchFrom = 0;
        while (true)
        {
            int open = text.IndexOf("<think>", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf("</think>", open + "<think>".Length, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                break;
            }

            collected.Append(text.Substring(open + "<think>".Length, close - open - "<think>".Length).Trim());
            collected.Append('\n');
            searchFrom = close + "</think>".Length;
            if (collected.Length >= MaximumReasoningChars)
            {
                break;
            }
        }

        if (collected.Length == 0)
        {
            return null;
        }

        // Keep the tail: the final deliberation is the most relevant excerpt.
        var excerpt = collected.ToString().Trim();
        if (excerpt.Length > MaximumReasoningChars)
        {
            excerpt = excerpt[^MaximumReasoningChars..];
        }

        return excerpt.Length == 0 ? null : excerpt;
    }

    /// <summary>
    /// Builds the metadata-only trace line for a tool-call assembly failure. The line
    /// carries counts, flags, lengths and generated ids only -- never user content,
    /// prompts, arguments or model text -- so emitting it to the process stderr log is
    /// safe. The step now runs in process, so this stderr line is the bounded record
    /// that tells the three <c>protocol_failure</c> branches apart after the fact.
    /// </summary>
    private static string ToolCallTrace(string site, string modelStepId, string fields) =>
        $"ru_model_step_trace site={site} step={modelStepId} code=protocol_failure {fields}";

    private static void EmitToolCallTrace(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            // Diagnostics must never change the step outcome.
        }
    }

    /// <summary>
    /// Parses one assembled tool-call arguments object. Assembled fragments arrive with
    /// whatever escaping the provider emitted, so a lone backslash that cannot start a
    /// valid escape (a Windows path streamed as <c>C:\Users\...</c>) gets one repair
    /// attempt before the call is rejected. The repair only doubles backslashes that
    /// have no valid reading at all; anything truncated or still unparseable fails
    /// exactly where it always did.
    /// </summary>
    private static JsonElement ParseToolArguments(string argumentsText)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("arguments must be an object");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            if (!JsonEscapeRepair.TryRepairInvalidEscapes(argumentsText, out var repaired))
            {
                throw;
            }

            using var fixedDocument = JsonDocument.Parse(repaired);
            if (fixedDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("arguments must be an object");
            }

            return fixedDocument.RootElement.Clone();
        }
    }

    private static (List<ModelToolCall> Calls, string? Text) ParseToolCalls(
        IReadOnlyList<ModelToolFragment> fragments,
        JsonObject settings,
        string finishReason,
        StringBuilder text,
        string modelStepId)
    {
        if (fragments.Count == 0)
        {
            var (promptSelection, visibleText) = ParsePromptToolSelection(text.ToString(), settings, modelStepId);
            if (promptSelection is not null)
            {
                return ([promptSelection], visibleText);
            }

            if (finishReason == "tool_calls")
            {
                // The provider announced a tool call and streamed none. Nothing
                // was half-parsed, so a retry cannot execute the wrong call --
                // unlike the ambiguous idless continuation below, which stays
                // closed on purpose, and unlike the resource caps above, where a
                // retry would only spend budget to hit the same ceiling. The
                // Host's bounded retry hands the reason back to the model, so
                // the next attempt is asked for a different shape. The trace
                // records the shape without carrying any user content.
                var trace = ToolCallTrace(
                    "zero_fragments",
                    modelStepId,
                    $"finishIsToolCalls=1 textLen={text.Length}");
                EmitToolCallTrace(trace);
                throw new ModelStepException("protocol_failure", retryable: true, trace: trace);
            }

            // VisibleText already carries the stripped prose when a trailing
            // block was removed; otherwise it is the full text (or empty).
            return (new List<ModelToolCall>(), string.IsNullOrEmpty(visibleText) ? null : visibleText);
        }

        var groups = new List<CallGroup> { new() };
        foreach (var fragment in fragments)
        {
            var hasProviderCallId = !string.IsNullOrEmpty(fragment.ProviderCallId);
            // A repeated call id is the ordinary continuation shape for gateways
            // that echo the id on every chunk: it names the call already in
            // progress, so it must not open a second, nameless call. The indexed
            // path accepts the same repetition (ChatToolCallAssembly), and the two
            // assemblies must agree on the same protocol.
            var repeatsCurrentCallId = hasProviderCallId
                && string.Equals(groups[^1].ProviderCallId, fragment.ProviderCallId, StringComparison.Ordinal);
            var opensNewCall = hasProviderCallId
                && !repeatsCurrentCallId
                && (groups[^1].ProviderCallId is not null
                    || groups[^1].ToolName is not null
                    || groups[^1].Arguments.Length > 0);
            if (opensNewCall)
            {
                groups.Add(new CallGroup());
            }

            var group = groups[^1];
            if (!hasProviderCallId)
            {
                // A fragment with no call id is the ordinary continuation of the
                // call already in progress: providers put the id and the tool
                // name on the first fragment only and stream the arguments after
                // it. Rejecting one merely because a second call had already
                // started meant any step with two tool calls died here, which is
                // the shape the Protocol explicitly allows. What is genuinely
                // ambiguous -- and still closed -- is an id-less fragment that
                // also names a tool while the current call already has data,
                // because that would be a second call that never got an id.
                var hasExistingCallData = group.ToolName is not null || group.Arguments.Length > 0;
                if (hasExistingCallData && !string.IsNullOrEmpty(fragment.ToolName))
                {
                    var trace = ToolCallTrace(
                        "chunk_conflict",
                        modelStepId,
                        $"groups={groups.Count} group={groups.Count - 1} hasId={(hasProviderCallId ? 1 : 0)} hasName={(!string.IsNullOrEmpty(fragment.ToolName) ? 1 : 0)} argsLen={fragment.ArgumentsFragment?.Length ?? 0}");
                    EmitToolCallTrace(trace);
                    throw new ModelStepException("protocol_failure", retryable: false, trace: trace);
                }
            }

            if (hasProviderCallId)
            {
                group.ProviderCallId = fragment.ProviderCallId;
            }

            if (!string.IsNullOrEmpty(fragment.ToolName))
            {
                group.ToolName ??= fragment.ToolName;
            }

            group.Arguments.Append(fragment.ArgumentsFragment);
        }

        var calls = new List<ModelToolCall>(groups.Count);
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (string.IsNullOrEmpty(group.ToolName))
            {
                var trace = ToolCallTrace(
                    "missing_name",
                    modelStepId,
                    $"groups={groups.Count} group={groupIndex} hasId={(group.ProviderCallId is null ? 0 : 1)} argsLen={group.Arguments.Length}");
                EmitToolCallTrace(trace);
                throw new ModelStepException("protocol_failure", retryable: true, trace: trace);
            }

            var tool = settings["model_tools"] is JsonArray modelTools
                ? modelTools.OfType<JsonObject>().FirstOrDefault(item => item["tool_id"]?.GetValue<string>() == group.ToolName)
                : null;
            var toolVersion = tool?["tool_version"]?.GetValue<string>();
            if (toolVersion is null)
            {
                throw new ModelStepException("model_tool_call_unavailable", retryable: true);
            }

            var argumentsText = group.Arguments.ToString();
            if (Encoding.UTF8.GetByteCount(argumentsText) > ModelGenerationBounds.MaxToolArgumentsBytes)
            {
                throw new ModelStepException("model_tool_call_invalid", retryable: true);
            }

            JsonElement arguments;
            try
            {
                arguments = ParseToolArguments(argumentsText);
            }
            catch (JsonException error)
            {
                throw new ModelStepException("model_tool_call_invalid", retryable: true, error);
            }

            var providerCallId = group.ProviderCallId
                ?? $"ru_call_{modelStepId}_{calls.Count + 1}";

            try
            {
                calls.Add(new ModelToolCall(providerCallId, group.ToolName, toolVersion, arguments));
            }
            catch (ArgumentException error)
            {
                throw new ModelStepException("protocol_failure", retryable: false, error);
            }
        }

        try
        {
            // Provider-native calls may carry narration before the call. It has already
            // streamed as deltas and it belongs to the step: dropping it here was the only
            // reason the feed stayed silent until the run finished. Raw call markup stays
            // out — narration is prose, never the call written out by hand.
            var narration = text.ToString().Trim();
            return (calls, narration.Length == 0 || IsToolCallMarkup(narration) ? null : narration);
        }
        catch (ArgumentException error)
        {
            throw new ModelStepException("protocol_failure", retryable: false, error);
        }
    }

    private sealed class CallGroup
    {
        public string? ProviderCallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// Whether step text is the tool call written out by hand rather than prose.
    /// </summary>
    /// <remarks>
    /// Prompt-based selections already strip their call block before it can be seen; a native
    /// call whose text side is markup gets the same treatment, because raw call markup must
    /// never reach the chat whichever way the model chose to write it.
    /// </remarks>
    private static bool IsToolCallMarkup(string text)
    {
        var candidate = ModelToolSelectionParser.NormalizeCandidate(text);
        return candidate.Length != 0 && candidate[0] is '{' or '[';
    }

    private static (ModelToolCall? Call, string? VisibleText) ParsePromptToolSelection(
        string text,
        JsonObject settings,
        string modelStepId)
    {
        var modelTools = settings["model_tools"] as JsonArray;
        var hasTools = modelTools is { Count: > 0 };
        var advertised = AdvertisedToolIds(modelTools);

        var candidate = ModelToolSelectionParser.NormalizeCandidate(text);
        if (candidate.Length != 0 && candidate[0] is '{' or '[')
        {
            if (!hasTools && IsJsonObjectWithoutToolFields(candidate))
            {
                return (null, text);
            }

            return (BuildSelectionCall(modelTools, candidate, advertised, modelStepId), null);
        }

        // Trailing wrapper: prose first, tool call last. A complete trailing
        // block either executes (valid selection) or is stripped from visible
        // text (invalid selection) so raw tool-call markup never reaches chat.
        if (ModelToolSelectionParser.TrySplitTrailingBlock(text, out var prose, out var block))
        {
            try
            {
                var trailing = ModelToolSelectionParser.Parse(
                    ModelToolSelectionParser.NormalizeCandidate(block), advertised);
                return (BuildSelectionCall(modelTools, trailing, modelStepId), string.IsNullOrWhiteSpace(prose) ? null : prose);
            }
            catch (ModelToolSelectionException error) when (
                error.Code is "unknown_tool" or "invalid_tool_version")
            {
                throw new ModelStepException("capability_unavailable", retryable: false, error);
            }
            catch (ModelToolSelectionException error)
            {
                if (string.IsNullOrWhiteSpace(prose))
                {
                    throw new ModelStepException("invalid_selection", retryable: true, error);
                }

                return (null, prose);
            }
        }

        // Opener-led attempt anywhere (prose glued to an envelope, possibly with
        // mangled delimiters). A tool-keyed payload executes with the prose
        // kept visible; a broken tool-keyed payload fails loudly instead of
        // leaking machine syntax as a chat answer. Anything without a
        // tool-keyed payload falls through (discussion/trailing paths below).
        if (ModelToolSelectionParser.TryExtractCallCandidate(text, out var callProse, out var callJson))
        {
            try
            {
                var selected = ModelToolSelectionParser.Parse(
                    ModelToolSelectionParser.NormalizeCandidate(callJson), advertised);
                return (BuildSelectionCall(modelTools, selected, modelStepId),
                    string.IsNullOrWhiteSpace(callProse) ? null : callProse);
            }
            catch (ModelToolSelectionException error) when (
                error.Code is "unknown_tool" or "invalid_tool_version")
            {
                throw new ModelStepException("capability_unavailable", retryable: false, error);
            }
            catch (ModelToolSelectionException error)
            {
                throw new ModelStepException("invalid_selection", retryable: true, error);
            }
        }

        if (!hasTools && IsJsonObjectWithoutToolFields(candidate))
        {
            return (null, text);
        }

        // A lone tool-call attempt in a non-JSON dialect (e.g. XML-ish tags the
        // model invented instead of the JSON envelope) must fail loudly instead
        // of being presented as a chat answer: the model tried to act, so
        // silent prose would mislead the user into thinking it answered.
        // Mid-text syntax discussion (tag not leading) stays visible.
        if (text.TrimStart().StartsWith("<tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelStepException("invalid_selection", retryable: true);
        }

        return (null, text);
    }

    private static HashSet<string> AdvertisedToolIds(JsonArray? modelTools) =>
        modelTools?
            .OfType<JsonObject>()
            .Select(tool => tool["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                    ? id
                    : string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    private static ModelToolCall BuildSelectionCall(
        JsonArray? modelTools,
        string candidate,
        HashSet<string> advertised,
        string modelStepId)
    {
        ModelToolSelection selection;
        try
        {
            selection = ModelToolSelectionParser.Parse(candidate, advertised);
        }
        catch (ModelToolSelectionException error) when (
            error.Code is "unknown_tool" or "invalid_tool_version")
        {
            throw new ModelStepException("capability_unavailable", retryable: false, error);
        }
        catch (ModelToolSelectionException error)
        {
            throw new ModelStepException("invalid_selection", retryable: true, error);
        }

        return BuildSelectionCall(modelTools, selection, modelStepId);
    }

    private static ModelToolCall BuildSelectionCall(
        JsonArray? modelTools,
        ModelToolSelection selection,
        string modelStepId)
    {
        var configuredTool = modelTools?
            .OfType<JsonObject>()
            .FirstOrDefault(tool => tool["tool_id"] is JsonValue value
                && value.TryGetValue<string>(out var id)
                && string.Equals(id, selection.ToolId, StringComparison.Ordinal));
        var configuredVersion = configuredTool?["tool_version"] is JsonValue version
            && version.TryGetValue<string>(out var versionText)
                ? versionText
                : null;
        if (!string.Equals(configuredVersion, selection.ToolVersion, StringComparison.Ordinal))
        {
            throw new ModelStepException("capability_unavailable", retryable: false);
        }

        try
        {
            var providerCallId = $"ru_call_{modelStepId}_1";
            return new ModelToolCall(providerCallId, selection.ToolId, selection.ToolVersion, selection.Arguments);
        }
        catch (ArgumentException error)
        {
            throw new ModelStepException("invalid_selection", retryable: true, error);
        }
    }

    private static bool IsJsonObjectWithoutToolFields(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return !document.RootElement.EnumerateObject().Any(property =>
                property.Name is "tool_id" or "tool_version" or "arguments");
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
