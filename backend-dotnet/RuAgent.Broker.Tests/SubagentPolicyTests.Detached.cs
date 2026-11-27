using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>Detached children (ADR 0045) outlive a normal parent close, and nothing else.</summary>
public sealed partial class SubagentPolicyTests
{
    [Fact]
    public async Task Detached_child_keeps_the_parent_ceiling_after_the_parent_closes_while_an_attached_sibling_is_revoked()
    {
        var ceiling = EnvelopeTestSnapshots.StandardEmpty();
        await Open("parent", "parent", ceiling);
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());
        await Open("attached", "attached", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await BindDetached("detached", ceiling);
        await _runtime.BindDelegationAsync("run_parent", "run_attached", SubagentRole.Coding, default,
            new(ceiling.Version, EnvelopeTestSnapshots.HashOf(ceiling)), ceiling);

        await CloseParentRun();

        Assert.Equal("allow", (await Evaluate("detached", "list", new JsonObject { ["path"] = "." })).Decision!.Decision);
        var attached = await Evaluate("attached", "list", new JsonObject { ["path"] = "." });
        Assert.Equal("deny", attached.Decision!.Decision);
        Assert.Equal("subagent_parent_revoked", attached.Decision.ReasonCode);
    }

    [Fact]
    public async Task Detached_child_binds_after_the_parent_closes_only_from_a_retained_snapshot()
    {
        var ceiling = EnvelopeTestSnapshots.StandardEmpty();
        await Open("parent", "parent", ceiling);
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());
        await CloseParentRun();

        var denied = await Assert.ThrowsAsync<AgentProtocolException>(() => BindDetached("detached", ceiling));
        Assert.Equal("subagent_delegation_denied", denied.Code);
        var notLive = await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.RetainDetachedParentAsync("run_parent", default));
        Assert.Equal("subagent_delegation_denied", notLive.Code);
    }

    [Fact]
    public async Task Detached_child_admitted_after_the_parent_closed_evaluates_against_the_retained_snapshot()
    {
        var ceiling = EnvelopeTestSnapshots.StandardEmpty();
        await Open("parent", "parent", ceiling);
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await CloseParentRun();
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());

        await BindDetached("detached", ceiling);

        Assert.Equal("allow", (await Evaluate("detached", "list", new JsonObject { ["path"] = "." })).Decision!.Decision);
    }

    [Fact]
    public async Task Binding_a_detached_child_to_a_live_parent_retains_it_without_a_prior_spawn_record()
    {
        // After a Host restart nothing was retained at spawn; the rebind to the live parent must.
        var ceiling = EnvelopeTestSnapshots.StandardEmpty();
        await Open("parent", "parent", ceiling);
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());
        await BindDetached("detached", ceiling);

        await CloseParentRun();

        Assert.Equal("allow", (await Evaluate("detached", "list", new JsonObject { ["path"] = "." })).Decision!.Decision);
    }

    [Fact]
    public async Task Only_a_ceiling_bound_detached_bind_may_use_a_retained_snapshot()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await CloseParentRun();
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default));
        Assert.Equal("subagent_delegation_denied", error.Code);
    }

    [Fact]
    public async Task Closing_the_parent_session_revokes_a_detached_child()
    {
        var ceiling = EnvelopeTestSnapshots.StandardEmpty();
        await Open("parent", "parent", ceiling);
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await BindDetached("detached", ceiling);
        await CloseParentRun();

        await _runtime.CloseSessionAsync("parent", default);

        var decision = await Evaluate("detached", "list", new JsonObject { ["path"] = "." });
        Assert.Equal("deny", decision.Decision!.Decision);
        Assert.Equal("subagent_parent_revoked", decision.Decision.ReasonCode);
    }

    [Fact]
    public async Task Cancelling_a_detached_child_by_its_own_run_revokes_it()
    {
        var ceiling = EnvelopeTestSnapshots.StandardEmpty();
        await Open("parent", "parent", ceiling);
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await BindDetached("detached", ceiling);
        await CloseParentRun();

        _runtime.CancelRun("run_detached");

        Assert.Equal("deny", (await Evaluate("detached", "list", new JsonObject { ["path"] = "." })).Decision!.Decision);
    }

    [Fact]
    public async Task A_live_parent_policy_change_still_revokes_a_detached_child()
    {
        var ask = PolicyEvalSnapshots.Rule("parent-ask", "ask", PolicyEvalSnapshots.Matcher(toolId: "list"));
        var ceiling = EnvelopeTestSnapshots.StandardWithRules(ask);
        await Open("parent", "parent", ceiling);
        await Open("detached", "detached", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await BindDetached("detached", ceiling);
        var denied = PolicyEvalSnapshots.Rule("new-rule", "deny", PolicyEvalSnapshots.Matcher(toolId: "list"), scope: "run");
        var changed = EnvelopeTestSnapshots.Snapshot(2, "standard", [ask, denied]);
        var rebound = await _runtime.RebindRunPolicyAsync(new AgentEnvelope(2, "rebind", AgentMessageTypes.RunPolicyRebind,
            "parent", null, 10000, EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", Path.Combine(_root, "parent"), changed)), default);
        Assert.Null(rebound.Error);

        await CloseParentRun();

        // Revoked while the parent was live; the retained snapshot cannot revive it.
        Assert.Equal("deny", (await Evaluate("detached", "list", new JsonObject { ["path"] = "." })).Decision!.Decision);
    }

    private Task BindDetached(string id, BrokerPolicySnapshotWire ceiling) =>
        _runtime.BindDelegationAsync("run_parent", "run_" + id, SubagentRole.Coding, default,
            new(ceiling.Version, EnvelopeTestSnapshots.HashOf(ceiling)), ceiling, detached: true);

    private async Task CloseParentRun()
    {
        var closed = await _runtime.CloseRunAsync(new AgentEnvelope(2, "close-parent", AgentMessageTypes.RunClose, "parent", null, 10000,
            new JsonObject { ["run_id"] = "run_parent" }), default);
        Assert.True(closed.Closed);
    }
}
