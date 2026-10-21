using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuAgent.Client;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>One remembered rule as the Settings list shows it.</summary>
public sealed record ApprovalRuleRow(string RuleId, string Title, string Detail, string ScopeLabel, string Decision)
{
    public bool HasDetail => Detail.Length > 0;

    public string AutomationName => HasDetail ? $"{Title}, {Detail}, {ScopeLabel}" : $"{Title}, {ScopeLabel}";
}

/// <summary>A workspace the user has sessions in, whose project rules are worth listing.</summary>
public sealed record ApprovalRuleWorkspace(string WorkspaceId, string Label);

/// <summary>
/// The approval rules the Host remembers: "Always allow" choices made on approval cards
/// (project rules) and imported global rules. The list is read-only; removing a rule only
/// narrows what runs without asking, so it never widens a permission.
/// </summary>
/// <remarks>
/// The Host lists global rules by default and project rules per workspace id, so the
/// list asks once for globals and once per workspace the user has sessions in. A failed
/// read says so and keeps the rows it had; it never shows an empty list as if no rule
/// existed.
/// </remarks>
public sealed partial class ApprovalRulesViewModel : ObservableObject
{
    private readonly Func<IApprovalRulesClient?> _client;
    private readonly Func<IReadOnlyList<ApprovalRuleWorkspace>> _workspaces;

    public ApprovalRulesViewModel(
        Func<IApprovalRulesClient?> client,
        Func<IReadOnlyList<ApprovalRuleWorkspace>> workspaces)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    }

    public ObservableCollection<ApprovalRuleRow> Rules { get; } = new();

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; private set; } = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    [ObservableProperty] public partial bool HasNoRules { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_client() is not { } client)
        {
            StatusText = "Rules are available once the agent service is connected.";
            return;
        }

        IsLoading = true;
        try
        {
            var rows = new List<ApprovalRuleRow>();
            foreach (var item in await client.ListPoliciesAsync("global", null, cancellationToken))
            {
                if (Map(item, "All projects") is { } row) rows.Add(row);
            }

            foreach (var workspace in _workspaces().DistinctBy(workspace => workspace.WorkspaceId))
            {
                foreach (var item in await client.ListPoliciesAsync("project", workspace.WorkspaceId, cancellationToken))
                {
                    if (Map(item, workspace.Label) is { } row) rows.Add(row);
                }
            }

            Rules.Clear();
            foreach (var row in rows.DistinctBy(row => row.RuleId)) Rules.Add(row);
            HasNoRules = Rules.Count == 0;
            StatusText = string.Empty;
        }
        catch (AgentApiException exception)
        {
            StatusText = $"Could not load the rules ({exception.ErrorCode}).";
        }
        catch (HttpRequestException)
        {
            StatusText = "Could not reach the agent service to load the rules.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(ApprovalRuleRow? row)
    {
        if (row is null || _client() is not { } client) return;
        try
        {
            await client.DeletePolicyAsync(row.RuleId, CancellationToken.None);
            Rules.Remove(row);
            HasNoRules = Rules.Count == 0;
            StatusText = string.Empty;
        }
        catch (AgentApiException exception)
        {
            StatusText = $"Could not remove the rule ({exception.ErrorCode}). It is still active.";
        }
        catch (HttpRequestException)
        {
            StatusText = "Could not reach the agent service. The rule is still active.";
        }
    }

    /// <summary>Turns one Host rule into a row, or null when it carries no id.</summary>
    internal static ApprovalRuleRow? Map(JsonElement item, string scopeLabel)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string ruleId = Text(item, "rule_id");
        if (ruleId.Length == 0) return null;
        string decision = Text(item, "decision") is { Length: > 0 } value ? value : "allow";
        var matcher = item.TryGetProperty("matcher", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        string tool = matcher.ValueKind == JsonValueKind.Object ? Text(matcher, "tool_id") : string.Empty;

        string verb = decision switch { "deny" => "Deny", "ask" => "Ask before", _ => "Allow" };
        string subject = tool switch
        {
            "bash" => "terminal command",
            "write" or "edit" => "file change",
            "read" or "glob" or "grep" => "file read",
            "web_fetch" or "web_search" => "web access",
            "" => "action",
            _ => tool,
        };

        string detail = string.Empty;
        if (matcher.ValueKind == JsonValueKind.Object)
        {
            string executable = Text(matcher, "resolved_executable");
            string argv = Text(matcher, "argv_pattern");
            string command = string.Join(' ', new[] { Path.GetFileName(executable), argv }.Where(part => part.Length > 0));
            detail = FirstNonEmpty(
                command,
                Text(matcher, "canonical_path_pattern"),
                Text(matcher, "cwd_pattern") is { Length: > 0 } cwd ? $"in {cwd}" : string.Empty,
                Text(matcher, "network_intent"),
                Text(matcher, "operation_class"));
        }

        return new ApprovalRuleRow(
            ruleId,
            $"{verb} {subject}",
            AgentApprovalSummaryPresentation.Bounded(detail, 120),
            scopeLabel,
            decision);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => value.Length > 0) ?? string.Empty;
}
