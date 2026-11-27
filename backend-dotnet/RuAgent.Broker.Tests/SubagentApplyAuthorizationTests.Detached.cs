using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>A later root may apply a detached child's result only within the creating root's session (ADR 0045).</summary>
public sealed partial class SubagentApplyAuthorizationTests
{
    [Fact]
    public async Task A_later_root_of_the_same_session_may_apply_a_detached_result()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        await _runtime.RetainDetachedParentAsync("run_parent", default);
        await CloseParent();
        await OpenLater("run_later", "parent");

        var decision = await _runtime.EvaluateSubagentApplyAsync("parent", "run_later", Changes(), default);

        Assert.Equal("allow", decision.Decision!.Decision);
        Assert.NotNull(decision.Decision.AuthorizationToken);
    }

    [Theory]
    [InlineData(false, "parent")]
    [InlineData(true, "other")]
    public async Task Without_a_retained_detached_parent_in_the_session_a_foreign_result_is_denied(bool retained, string session)
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        if (retained) await _runtime.RetainDetachedParentAsync("run_parent", default);
        await CloseParent();
        await OpenLater("run_later", session);

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.EvaluateSubagentApplyAsync(session, "run_later", Changes(), default));

        Assert.Equal("subagent_apply_denied", error.Code);
        Assert.Equal(0, _journal.Begins);
    }

    private async Task CloseParent()
    {
        var closed = await _runtime.CloseRunAsync(Envelope("close", AgentMessageTypes.RunClose,
            new JsonObject { ["run_id"] = "run_parent" }), default);
        Assert.True(closed.Closed);
    }

    private async Task OpenLater(string runId, string session)
    {
        var result = await _runtime.OpenRunAsync(new AgentEnvelope(2, "open-" + runId, AgentMessageTypes.RunOpen, session, null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload(runId, session, _root, EnvelopeTestSnapshots.StandardEmpty())), default);
        Assert.Null(result.Error);
    }
}
