using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

/// <summary>Explicit saved-allowance revocation using the existing Host policy APIs.</summary>
internal sealed class ChatApprovalRulesWorkflow(AgentApiClient api, SessionApiClient sessions)
{
    private const int MaxRules = 1000;
    private const int MaxPages = 10;
    private sealed record Listing(IReadOnlyList<ChatApprovalRule> Rules, string? ProjectNotice);

    internal async Task ExecuteAsync(string workspace,
        Func<ChoiceList, CancellationToken, Task<string?>>? picker,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? document,
        TextWriter output, bool json, CancellationToken ct)
    {
        while (true)
        {
            var listing = await LoadAsync(workspace, ct).ConfigureAwait(false);
            if (picker is null)
            {
                if (json)
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = "remembered_rules",
                        project_notice = listing.ProjectNotice,
                        items = listing.Rules.Select(rule => new { rule_id = rule.Id, scope = rule.Scope,
                            workspace_id = rule.WorkspaceId, decision = rule.Decision, title = rule.Title, detail = rule.Detail }) })).ConfigureAwait(false);
                else
                {
                    await output.WriteLineAsync(listing.ProjectNotice ?? "Global and current-workspace rules.").ConfigureAwait(false);
                    foreach (var rule in listing.Rules)
                        await output.WriteLineAsync($"{rule.Id} · {rule.ScopeLabel} · {rule.Title} · {rule.Detail}").ConfigureAwait(false);
                    if (listing.Rules.Count == 0) await output.WriteLineAsync("No saved rules in the available scopes.").ConfigureAwait(false);
                    await output.WriteLineAsync("Open /preset in the interactive TUI to inspect or remove saved allowances.").ConfigureAwait(false);
                }
                return;
            }
            var items = listing.Rules.Select((rule, index) => new ChoiceItem(rule.Title,
                rule.ScopeLabel + " · " + TuiText.Truncate(rule.Detail, 120), "rule:" + index)).ToList();
            if (items.Count == 0) items.Add(new("No saved rules", "No saved rules in the available scopes", "empty", false));
            if (listing.ProjectNotice is { } note) items.Add(new("Project rules unavailable", note, "notice", false));
            items.Add(new("Refresh", "Reload saved rules", "refresh"));
            var chosen = await picker(new ChoiceList("Remembered rules", items,
                footer: "enter details · esc closes · preset unchanged"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (chosen is null) return;
            if (chosen == "refresh") continue;
            if (!chosen.StartsWith("rule:", StringComparison.Ordinal)
                || !int.TryParse(chosen.AsSpan(5), out var index) || index < 0 || index >= listing.Rules.Count)
                throw new AgentApiException("rules_selection_invalid", "Refresh saved rules.");
            if (!await InspectAsync(listing.Rules[index], workspace, picker, document, output, ct).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> InspectAsync(ChatApprovalRule rule, string workspace,
        Func<ChoiceList, CancellationToken, Task<string?>> picker,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? document, TextWriter output, CancellationToken ct)
    {
        while (true)
        {
            var action = await picker(new ChoiceList(rule.Title,
            [
                new("View details", rule.ScopeLabel + " · " + rule.Id, "details"),
                new("Remove saved allowance", rule.CanRemove ? rule.Detail : "Ask/Deny rules are read-only here", "remove", rule.CanRemove),
                new("Back to rules", "Keep this rule", "back"),
            ], footer: "enter selects · esc closes"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (action is null) return false;
            if (action == "back") return true;
            if (action == "details")
            {
                if (document is not null) await document("Saved approval rule", rule.Details(), ct).ConfigureAwait(false);
                else foreach (var line in rule.Details()) await output.WriteLineAsync(line).ConfigureAwait(false);
                continue;
            }
            if (action != "remove" || !rule.CanRemove)
                throw new AgentApiException("rule_removal_refused", "Only saved Allow rules can be removed here.");
            var confirmed = await picker(new ChoiceList("Remove saved allowance?",
            [
                new("Keep rule", rule.ScopeLabel + " · " + rule.Id, "keep"),
                new("Remove this allowance", rule.Detail, "confirm_remove"),
            ], selected: 0, footer: "Other grants and the current preset stay unchanged · esc cancels"), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (confirmed != "confirm_remove") return confirmed is not null;
            if (rule.WorkspaceId is not null && await WorkspaceIdAsync(workspace, ct).ConfigureAwait(false) != rule.WorkspaceId)
                throw new AgentApiException("rule_scope_changed", "Workspace identity changed; refresh saved rules.");
            var current = await ReadRulesAsync(rule.Scope, rule.WorkspaceId, ct).ConfigureAwait(false);
            if (!current.Any(item => item.Id == rule.Id && item.CanRemove && item.Source.GetRawText() == rule.Source.GetRawText()))
                throw new AgentApiException("rule_changed", "The rule changed or was removed; refresh saved rules.");
            ct.ThrowIfCancellationRequested();
            // One request only. A lost acknowledgement must not trigger an automatic retry.
            await api.DeletePolicyAsync(rule.Id, interactive: true, ct).ConfigureAwait(false);
            if ((await ReadRulesAsync(rule.Scope, rule.WorkspaceId, ct).ConfigureAwait(false)).Any(item => item.Id == rule.Id))
                throw new AgentApiException("rule_removal_unconfirmed", "The rule is still listed; refresh before retrying.");
            await output.WriteLineAsync("Removed saved allowance: " + rule.Id).ConfigureAwait(false);
            return true;
        }
    }

    private async Task<Listing> LoadAsync(string workspace, CancellationToken ct)
    {
        var rules = (await ReadRulesAsync("global", null, ct).ConfigureAwait(false)).ToList();
        string? notice;
        try
        {
            var id = await WorkspaceIdAsync(workspace, ct).ConfigureAwait(false);
            notice = id is null ? "No saved Host workspace identity found. Global rules remain available." : null;
            if (id is not null) rules.AddRange(await ReadRulesAsync("project", id, ct).ConfigureAwait(false));
        }
        catch (AgentApiException error) { notice = "Could not read project rules (" + ChatDisplayText.Sanitize(error.ErrorCode, 96) + ")."; }
        catch (HttpRequestException) { notice = "Could not reach the Host for project rules."; }
        if (rules.Count > MaxRules || rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new AgentApiException("rules_response_invalid", "Saved rule identity or count is invalid.");
        return new(rules, notice);
    }

    private async Task<IReadOnlyList<ChatApprovalRule>> ReadRulesAsync(string scope, string? workspace, CancellationToken ct)
    {
        var items = await api.ListPoliciesAsync(scope, workspace, ct).ConfigureAwait(false);
        if (items.Count > MaxRules) throw new AgentApiException("rules_limit_exceeded", "Saved rule list exceeded its display budget.");
        var rules = items.Select(item => ChatApprovalRule.Parse(item, scope, workspace)).ToArray();
        if (rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length)
            throw new AgentApiException("rules_response_invalid", "Duplicate saved rule identity.");
        return rules;
    }

    private async Task<string?> WorkspaceIdAsync(string workspace, CancellationToken ct)
    {
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await sessions.ListPageAsync(cursor, limit: 200, cancellationToken: ct).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("sessions", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new AgentApiException("rules_workspace_unavailable", "Session metadata is unavailable.");
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) throw new AgentApiException("rules_workspace_unavailable", "Invalid session metadata.");
                var path = ChatApprovalRule.Text(row, "workspace_path");
                if (!Path.IsPathFullyQualified(path)) continue;
                string candidate;
                try { candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (!candidate.Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
                var id = ChatApprovalRule.Text(row, "workspace_id");
                if (!ChatApprovalRule.IsId(id, "ws_")) continue;
                ids.Add(id);
            }
            if (ids.Count > 1) throw new AgentApiException("rules_workspace_ambiguous", "Workspace identity is ambiguous.");
            if (!result.TryGetProperty("next_cursor", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                if (result.TryGetProperty("sessions_truncated", out var truncated) && truncated.ValueKind != JsonValueKind.False)
                    throw new AgentApiException("rules_workspace_unavailable", "Session metadata listing is incomplete.");
                return ids.SingleOrDefault();
            }
            if (next.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cursor = next.GetString()) || !cursors.Add(cursor))
                throw new AgentApiException("rules_workspace_unavailable", "Session metadata cursor is invalid.");
        }
        throw new AgentApiException("rules_workspace_limit", "Session metadata listing is incomplete.");
    }
}
