using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;

namespace RuAgent.Cli;

internal sealed record ChatApprovalRule(string Id, string Scope, string? WorkspaceId,
    string Decision, string Title, string Detail, JsonElement Source)
{
    internal bool CanRemove => Decision == "allow";
    internal string ScopeLabel => Scope == "global" ? "All projects" : "Current workspace";

    internal static bool IsId(string? value, string prefix) => value is not null
        && value.Length == prefix.Length + 32 && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static ChatApprovalRule Parse(JsonElement source, string scope, string? workspace)
    {
        if (source.ValueKind != JsonValueKind.Object || !IsId(Text(source, "rule_id"), "rule_")
            || Text(source, "scope") != scope || Text(source, "decision") is not ("allow" or "ask" or "deny")
            || !source.TryGetProperty("matcher", out var matcher) || matcher.ValueKind != JsonValueKind.Object)
            throw new AgentApiException("rules_response_invalid", "Saved rule response has an invalid identity or scope.");
        var decision = Text(source, "decision");
        var tool = Text(matcher, "tool_id");
        var subject = tool.Length == 0 ? "any matching action" : tool;
        var detail = string.Join(" · ", matcher.EnumerateObject()
            .Where(field => field.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(field => field.Name + ": " + (field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText())));
        return new(Text(source, "rule_id"), scope, workspace, decision,
            ChatDisplayText.Sanitize(decision + " · " + subject, 128),
            ChatDisplayText.Sanitize(detail, 2048), source.Clone());
    }

    internal IReadOnlyList<string> Details() =>
    [
        Title, "Scope: " + ScopeLabel, "Rule: " + Id,
        .. (WorkspaceId is null ? Array.Empty<string>() : new[] { "Workspace: " + WorkspaceId }),
        "", .. JsonSerializer.Serialize(Source, new JsonSerializerOptions { WriteIndented = true })
            .Split('\n').Select(line => ChatDisplayText.Sanitize(line, 8192)),
        "", CanRemove ? "Removing this saved allowance does not change your preset or clear other grants."
            : "Read-only: removing an Ask or Deny rule could permit more actions.",
    ];

    internal static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
