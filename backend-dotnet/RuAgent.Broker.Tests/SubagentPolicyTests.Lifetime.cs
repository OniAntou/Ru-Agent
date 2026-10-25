using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_survives_duplicate_open_and_policy_rebind(bool rebind)
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        _runtime.CancelRun("run_parent");
        if (rebind)
        {
            var result = await _runtime.RebindRunPolicyAsync(new(2, "rebind", AgentMessageTypes.RunPolicyRebind,
                "parent", null, 10000, EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent",
                    Path.Combine(_root, "parent"), EnvelopeTestSnapshots.StandardEmpty(2))), default);
            Assert.Null(result.Error);
        }
        else await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default));
        Assert.Equal("subagent_delegation_denied", error.Code);
    }

    [Fact]
    public async Task Reopening_child_run_before_session_teardown_cannot_erase_review_classification()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        await Open("child", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Review, default);
        var closed = await _runtime.CloseRunAsync(new(2, "close-child", AgentMessageTypes.RunClose,
            "child", null, 10000, new JsonObject { ["run_id"] = "run_child" }), default);
        Assert.True(closed.Closed);
        await Open("child", "parent", EnvelopeTestSnapshots.FullAccessEmpty());
        var decision = await Evaluate("child", "write_file", new JsonObject
            { ["path"] = "x.txt", ["content"] = "x" });
        Assert.Equal("deny", decision.Decision!.Decision);
        Assert.False(File.Exists(Path.Combine(_root, "parent", "x.txt")));
    }

    [Fact]
    public async Task Reopening_closed_parent_session_cannot_revive_a_surviving_child()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        await Open("child", "child", EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.BindDelegationAsync("run_parent", "run_child", SubagentRole.Coding, default);
        await _runtime.CloseSessionAsync("parent", default);
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());

        var decision = await Evaluate("child", "list", new JsonObject { ["path"] = "." });
        Assert.Equal("deny", decision.Decision!.Decision);
        Assert.Equal("subagent_parent_revoked", decision.Decision.ReasonCode);
    }

    [Fact]
    public async Task Closed_sessions_release_their_bindings_without_erasing_surviving_child_classification()
    {
        for (var i = 0; i < 24; i++)
        {
            var parent = "parent_" + i;
            var child = "child_" + i;
            await Open(parent, "parent", EnvelopeTestSnapshots.StandardEmpty());
            await Open(child, "child", EnvelopeTestSnapshots.StandardEmpty());
            await _runtime.BindDelegationAsync("run_" + parent, "run_" + child, SubagentRole.Coding, default);
            await _runtime.CloseSessionAsync(parent, default);
            Assert.Equal(1, RetainedBindings());
            var decision = await Evaluate(child, "list", new JsonObject { ["path"] = "." });
            Assert.Equal("deny", decision.Decision!.Decision);
            await _runtime.CloseSessionAsync(child, default);
            Assert.Equal(0, RetainedBindings());
        }
    }

    [Fact]
    public async Task Retained_binding_capacity_denies_without_eviction_and_is_released_by_session_close()
    {
        await Open("parent", "parent", EnvelopeTestSnapshots.StandardEmpty());
        for (var i = 0; i < 1024; i++)
        {
            var id = "review_" + i;
            await Open(id, "parent", EnvelopeTestSnapshots.StandardEmpty());
            await _runtime.BindDelegationAsync("run_parent", "run_" + id, SubagentRole.Review, default);
        }
        await Open("overflow", "parent", EnvelopeTestSnapshots.StandardEmpty());
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.BindDelegationAsync("run_parent", "run_overflow", SubagentRole.Review, default));
        Assert.Equal("subagent_delegation_capacity", error.Code);
        Assert.Equal(1024, RetainedBindings());
        var stillReview = await Evaluate("review_0", "write_file", new JsonObject
            { ["path"] = "x.txt", ["content"] = "x" });
        Assert.Equal("subagent_role_denied", stillReview.Decision!.ReasonCode);
        await _runtime.CloseSessionAsync("review_0", default);
        await _runtime.BindDelegationAsync("run_parent", "run_overflow", SubagentRole.Review, default);
        Assert.Equal(1024, RetainedBindings());
    }

    // Inspect retained ownership state: successful authorization alone cannot prove
    // that closed sessions release references in a long-lived Host process.
    private int RetainedBindings() => ((IDictionary)typeof(BrokerExecutionRuntime)
        .GetField("_delegations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_runtime)!).Count;
}
