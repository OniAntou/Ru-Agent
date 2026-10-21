using System.Text.Json;
using RuAgent.Client;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class ApprovalRulesViewModelTests
{
    private readonly FakeRules _client = new();
    private readonly List<ApprovalRuleWorkspace> _workspaces = [new("ws-1", @"D:\work\api"), new("ws-1", @"D:\work\api")];

    private ApprovalRulesViewModel NewViewModel() => new(() => _client, () => _workspaces);

    [Fact]
    public async Task Rules_are_read_once_for_globals_and_once_per_workspace()
    {
        _client.Global = [Rule("r-global", "allow", "bash", executable: @"C:\Program Files\Git\cmd\git.exe", argv: "status")];
        _client.Project["ws-1"] = [Rule("r-project", "allow", "write", path: "src/**")];
        var rules = NewViewModel();

        await rules.RefreshAsync();

        Assert.Equal([("global", (string?)null), ("project", "ws-1")], _client.Reads);
        Assert.Equal(["r-global", "r-project"], rules.Rules.Select(rule => rule.RuleId));
        var terminal = rules.Rules[0];
        Assert.Equal("Allow terminal command", terminal.Title);
        Assert.Equal("git.exe status", terminal.Detail);
        Assert.Equal("All projects", terminal.ScopeLabel);
        Assert.Equal("Allow file change", rules.Rules[1].Title);
        Assert.Equal("src/**", rules.Rules[1].Detail);
        Assert.Equal(@"D:\work\api", rules.Rules[1].ScopeLabel);
        Assert.False(rules.HasNoRules);
    }

    [Fact]
    public async Task No_rules_is_said_plainly()
    {
        var rules = NewViewModel();

        await rules.RefreshAsync();

        Assert.Empty(rules.Rules);
        Assert.True(rules.HasNoRules);
        Assert.False(rules.HasStatus);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_rows_and_does_not_claim_there_are_none()
    {
        _client.Global = [Rule("r-global", "deny", "web_fetch", network: "example.com")];
        var rules = NewViewModel();
        await rules.RefreshAsync();

        _client.Failure = new AgentApiException("policy_list_failed", "boom", 500);
        await rules.RefreshAsync();

        Assert.Single(rules.Rules);
        Assert.False(rules.HasNoRules);
        Assert.Contains("policy_list_failed", rules.StatusText, StringComparison.Ordinal);
        Assert.Equal("Deny web access", rules.Rules[0].Title);
    }

    [Fact]
    public async Task Removing_a_rule_deletes_it_on_the_host_then_drops_the_row()
    {
        _client.Global = [Rule("r-1", "allow", "bash", executable: "npm.cmd", argv: "test")];
        var rules = NewViewModel();
        await rules.RefreshAsync();

        await rules.RemoveCommand.ExecuteAsync(rules.Rules[0]);

        Assert.Equal(["r-1"], _client.Deleted);
        Assert.Empty(rules.Rules);
        Assert.True(rules.HasNoRules);
    }

    [Fact]
    public async Task A_failed_removal_keeps_the_row_and_says_the_rule_is_still_active()
    {
        _client.Global = [Rule("r-1", "allow", "bash", executable: "npm.cmd", argv: "test")];
        var rules = NewViewModel();
        await rules.RefreshAsync();
        _client.Failure = new AgentApiException("policy_delete_failed", "no", 409);

        await rules.RemoveCommand.ExecuteAsync(rules.Rules[0]);

        Assert.Single(rules.Rules);
        Assert.Contains("still active", rules.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_without_an_id_is_not_listed()
    {
        using var document = JsonDocument.Parse("""{"decision":"allow","matcher":{"tool_id":"bash"}}""");

        Assert.Null(ApprovalRulesViewModel.Map(document.RootElement, "All projects"));
    }

    private static JsonElement Rule(
        string id, string decision, string tool,
        string? executable = null, string? argv = null, string? path = null, string? network = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            rule_id = id,
            scope = "global",
            decision,
            matcher = new
            {
                tool_id = tool,
                resolved_executable = executable,
                argv_pattern = argv,
                canonical_path_pattern = path,
                network_intent = network,
            },
        });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeRules : IApprovalRulesClient
    {
        public IReadOnlyList<JsonElement> Global { get; set; } = [];

        public Dictionary<string, IReadOnlyList<JsonElement>> Project { get; } = new();

        public List<(string? Scope, string? WorkspaceId)> Reads { get; } = new();

        public List<string> Deleted { get; } = new();

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<JsonElement>> ListPoliciesAsync(string? scope, string? workspaceId, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            Reads.Add((scope, workspaceId));
            return Task.FromResult(scope == "global"
                ? Global
                : Project.TryGetValue(workspaceId ?? string.Empty, out var rules) ? rules : []);
        }

        public Task DeletePolicyAsync(string ruleId, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            Deleted.Add(ruleId);
            return Task.CompletedTask;
        }
    }
}
