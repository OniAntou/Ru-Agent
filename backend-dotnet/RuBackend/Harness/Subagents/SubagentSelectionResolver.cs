using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed class SubagentConfigurationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Inheritance never reads settings. Overrides require authenticated user-prompt evidence.</summary>
public static class SubagentSelectionResolver
{
    public static async Task<AgentRunProviderSelection> ResolveAsync(AgentRunProviderSelection parent,
        SubagentRole role, string? model, string? provider, string userPrompt,
        Func<string, string?, CancellationToken, Task<AgentRunProviderSelection>> resolve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!Enum.IsDefined(role)) throw new SubagentConfigurationException("subagent_role_invalid");
        if (model is null && provider is null) return parent;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl)
            || !MentionsModel(userPrompt, model))
            throw new SubagentConfigurationException("subagent_model_user_request_required");
        var selected = await resolve(model, provider, ct).ConfigureAwait(false);
        if (!MatchesModel(selected.Model, model))
            throw new SubagentConfigurationException("subagent_model_override_mismatch");
        return selected;
    }

    public static string Normalize(string value) => string.Join(" ",
        System.Text.RegularExpressions.Regex.Matches(value, @"[\p{L}]+|[0-9]+")
            .Select(m => m.Value.ToLowerInvariant()));
    public static bool MatchesModel(string catalogId, string requested) =>
        Normalize(catalogId) == Normalize(requested)
        || Normalize(catalogId.Split('/').Last()) == Normalize(requested)
        || Normalize(System.Text.RegularExpressions.Regex.Replace(catalogId.Split('/').Last(), @"-\d{8}$", "")) == Normalize(requested);

    public static bool MentionsModel(string prompt, string requested)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(requested, @"[\p{L}\p{N}]+")
            .Select(m => System.Text.RegularExpressions.Regex.Escape(m.Value));
        var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[^\p{L}\p{N}]*", words) + @"(?![\p{L}\p{N}])";
        return Normalize(requested).Length > 0 && System.Text.RegularExpressions.Regex.Matches(prompt, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)).Any(m => Normalize(m.Value) == Normalize(requested));
    }
}
