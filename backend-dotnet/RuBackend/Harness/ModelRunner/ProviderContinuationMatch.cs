using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness.ModelRunner;

/// <summary>
/// Shared continuation matching for the two native-history projectors. The
/// call-batch comparison and the identity guard live here so a change to one
/// protocol's matching rules cannot silently drift from the other's.
/// </summary>
internal static class ProviderContinuationMatch
{
    /// <summary>
    /// The continuation that belongs to one Responses tool batch: same pinned
    /// identity as the projection (provider, protocol, model) and identical
    /// provider call ids, names and parsed arguments.
    /// </summary>
    public static ProviderContinuation? FindOpenAi(
        IReadOnlyList<ProviderContinuation> continuations,
        HashSet<ProviderContinuation> used,
        IReadOnlyList<ModelFunctionCall> calls,
        string model) =>
        continuations.FirstOrDefault(candidate => !used.Contains(candidate)
            && candidate.Provider == "openai"
            && candidate.Protocol == "openai_responses"
            && candidate.Model == model
            && MatchesOpenAiCalls(candidate, calls));

    /// <summary>
    /// The continuation that belongs to one Anthropic tool batch.
    /// </summary>
    public static ProviderContinuation? FindAnthropic(
        IReadOnlyList<ProviderContinuation> continuations,
        HashSet<ProviderContinuation> used,
        IReadOnlyList<ModelFunctionCall> calls,
        string model) =>
        continuations.FirstOrDefault(candidate => !used.Contains(candidate)
            && candidate.Provider == "anthropic"
            && candidate.Protocol == "anthropic_messages"
            && candidate.Model == model
            && MatchesAnthropicCalls(candidate, calls));

    private static bool MatchesOpenAiCalls(
        ProviderContinuation continuation,
        IReadOnlyList<ModelFunctionCall> calls)
    {
        var providerCalls = continuation.OutputItems
            .Where(item => item["type"]?.GetValue<string>() == "function_call")
            .ToArray();
        if (providerCalls.Length != calls.Count) return false;
        for (var index = 0; index < calls.Count; index++)
        {
            var arguments = providerCalls[index]["arguments"]?.GetValue<string>();
            if (providerCalls[index]["call_id"]?.GetValue<string>() != calls[index].Id
                || providerCalls[index]["name"]?.GetValue<string>() != calls[index].Name
                || arguments is null
                || !JsonNode.DeepEquals(
                    JsonNode.Parse(arguments),
                    JsonNode.Parse(calls[index].Arguments.GetRawText())))
                return false;
        }

        return true;
    }

    private static bool MatchesAnthropicCalls(
        ProviderContinuation continuation,
        IReadOnlyList<ModelFunctionCall> calls)
    {
        var providerCalls = continuation.OutputItems
            .Where(item => item["type"]?.GetValue<string>() == "tool_use")
            .ToArray();
        if (providerCalls.Length != calls.Count) return false;
        for (var index = 0; index < calls.Count; index++)
        {
            var input = providerCalls[index]["input"];
            if (providerCalls[index]["id"]?.GetValue<string>() != calls[index].Id
                || providerCalls[index]["name"]?.GetValue<string>() != calls[index].Name
                || input is not JsonObject
                || !JsonNode.DeepEquals(input, JsonNode.Parse(calls[index].Arguments.GetRawText())))
                return false;
        }

        return true;
    }
}
