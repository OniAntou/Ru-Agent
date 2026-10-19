using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentPolicyTests : IDisposable
{
    [Theory]
    [InlineData("allow", "deny")]
    [InlineData("deny", "deny")]
    public async Task Policy_rebind_retains_ceiling_or_revokes_child(string addedDecision, string expected)
    {
        var ask = PolicyEvalSnapshots.Rule("parent-ask", "ask", PolicyEvalSnapshots.Matcher(toolId: "list"));
        var ceiling = EnvelopeTestSnapshots.StandardWithRules(ask);
        await Open("parent", "parent", ceiling);
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default,
            new(ceiling.Version, EnvelopeTestSnapshots.HashOf(ceiling)), ceiling);
        var additional = PolicyEvalSnapshots.Rule("new-rule", addedDecision, PolicyEvalSnapshots.Matcher(toolId: "list"), scope: "run");
        var changed = EnvelopeTestSnapshots.Snapshot(2, "standard", [ask, additional]);
        var rebound = await _runtime.RebindRunPolicyAsync(new AgentEnvelope(2, "rebind", AgentMessageTypes.RunPolicyRebind,
            "parent", null, 10000, EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", Path.Combine(_root, "parent"), changed)), default);
        Assert.Null(rebound.Error);
        Assert.Equal(expected, (await Evaluate("child", "list", new JsonObject { ["path"] = "." })).Decision!.Decision);
    }

    [Fact]
    public async Task Parent_remembered_allow_is_not_copied_into_child_ceiling()
    {
        var ask = PolicyEvalSnapshots.Rule("parent-ask", "ask", PolicyEvalSnapshots.Matcher(toolId: "list"));
        var remembered = PolicyEvalSnapshots.Rule("parent-approved", "allow", PolicyEvalSnapshots.Matcher(toolId: "list"), scope: "run");
        var ceiling = EnvelopeTestSnapshots.StandardWithRules(ask);
        var effective = EnvelopeTestSnapshots.Snapshot(2, "standard", [ask, remembered]);
        await Open("parent", "parent", effective);
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default,
            new(ceiling.Version, EnvelopeTestSnapshots.HashOf(ceiling)), ceiling);
        var blocked = (await Evaluate("child", "list", new JsonObject { ["path"] = "." })).Decision!;
        Assert.Equal("deny", blocked.Decision);
        Assert.Equal("subagent_parent_action_required", blocked.ReasonCode);
        Assert.Null(blocked.ApprovalId);
    }

    [Fact]
    public async Task Recovery_cannot_bind_against_a_different_parent_policy()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.BindDelegationAsync(
            "run_parent", "run_child", SubagentRole.Coding, default,
            new SubagentParentPolicyIdentity(99, new string('a', 64))));
    }

    [Fact]
    public async Task Workspace_provisioning_requires_active_root_coding_authority_before_git()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.Snapshot(1, "plan", []));
        var state = Path.Combine(_root, "state");
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.ProvisionSubagentWorkspaceAsync("run_parent", "agent_one", state, default));
        Assert.Equal("subagent_delegation_denied", error.Code);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public async Task Even_pathless_tools_must_use_the_bound_child_workspace_context()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        Assert.True(_session.TryGetRunLease("run_parent", out var parent));
        var scope = new RuAgent.Broker.Subagents.DelegatedExecutionScope("run_parent", "run_child", "parent", "child",
            Path.Combine(_root, "parent"), Path.Combine(_root, "child"), parent!.SnapshotVersion, parent.SnapshotHash, SubagentRole.Coding);
        var context = new PolicyContextFactory(parent).Create("web_search", "1.0.0", new JsonObject { ["query"] = "test" });
        var result = RuAgent.Broker.Subagents.DelegationPolicyEvaluator.Evaluate(scope, parent, context, DateTimeOffset.UtcNow);
        Assert.Equal("subagent_scope_denied", result.ReasonCode);
    }
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-delegation-" + Guid.NewGuid().ToString("N"));
    private readonly BrokerSession _session;
    private readonly ToolExecutionService _tools;
    private readonly BrokerExecutionRuntime _runtime;
    public SubagentPolicyTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "parent"));
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        _session = BrokerSession.CreateForHost(_root, DateTimeOffset.UtcNow.AddMinutes(30));
        _tools = new ToolExecutionService(_session, _root);
        _runtime = new BrokerExecutionRuntime(_session, _tools);
    }

    [Theory]
    [InlineData("write", "{\"op\":\"write\",\"path\":\"x.txt\",\"content\":\"x\"}")]
    [InlineData("bash", "{\"mode\":\"shell\",\"script\":\"echo unsafe\"}")]
    public async Task Reviewer_is_denied_by_runtime_even_with_full_access(string tool, string arguments)
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        await Open("child", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Review, default);
        var decision = await Evaluate("child", tool, JsonNode.Parse(arguments)!.AsObject());
        Assert.Equal("deny", decision.Decision!.Decision);
        Assert.Equal("subagent_role_denied", decision.Decision.ReasonCode);
    }

    [Fact]
    public async Task Coding_cannot_write_original_checkout_or_delegate_further()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        await Open("child", "child", EnvelopeTestSnapshots.FullAccessEmpty());
        await Open("grandchild", "child", EnvelopeTestSnapshots.FullAccessEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default);
        var denied = await Evaluate("child", "write", new JsonObject { ["op"] = "write", ["path"] = Path.Combine(_root, "parent", "x.txt"), ["content"] = "x" });
        Assert.Equal("subagent_scope_denied", denied.Decision!.ReasonCode);
        await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.BindDelegationAsync("run_child", "run_grandchild", SubagentRole.Coding, default));
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("ask")]
    public async Task Parent_absolute_rule_is_retained_in_relocated_workspace(string effect)
    {
        var path = Path.Combine(_root, "parent", "secret.txt");
        var rule = PolicyEvalSnapshots.Rule("parent-secret", effect, PolicyEvalSnapshots.Matcher(toolId: "read", pathPattern: path));
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardWithRules(rule));
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        File.WriteAllText(Path.Combine(_root, "child", "secret.txt"), "secret");
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default);
        var decision = await Evaluate("child", "read", new JsonObject { ["path"] = "secret.txt" });
        Assert.Equal("deny", decision.Decision!.Decision);
        if (effect == "ask") Assert.Equal("subagent_parent_action_required", decision.Decision.ReasonCode);
        Assert.Null(decision.Decision.ApprovalId);
    }

    [Fact]
    public async Task Closing_parent_invalidates_child_authorization()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default);
        await _runtime.CloseRunAsync(new AgentEnvelope(2, "close-parent", AgentMessageTypes.RunClose, "parent", null, 10000,
            new JsonObject { ["run_id"] = "run_parent" }), default);
        var decision = await Evaluate("child", "list", new JsonObject { ["path"] = "." });
        Assert.Equal("deny", decision.Decision!.Decision);
        Assert.Equal("subagent_parent_revoked", decision.Decision.ReasonCode);
    }

    private async Task Open(string id, string folder, BrokerPolicySnapshotWire snapshot)
    {
        var result = await _runtime.OpenRunAsync(new AgentEnvelope(2, "open-" + id, AgentMessageTypes.RunOpen, id, null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_" + id, id, Path.Combine(_root, folder), snapshot)), default);
        Assert.Null(result.Error);
    }

    private Task<ToolEvaluateResult> Evaluate(string id, string tool, JsonObject arguments) =>
        _runtime.EvaluateAsync(new AgentEnvelope(2, "eval-" + id, AgentMessageTypes.ToolEvaluate, id, null, 10000,
            new JsonObject { ["run_id"] = "run_" + id, ["tool_id"] = tool, ["tool_version"] = "1.0.0", ["arguments"] = arguments }), default);

    public void Dispose()
    {
        _runtime.Dispose();
        _tools.Dispose();
        Directory.Delete(_root, true);
    }
}
