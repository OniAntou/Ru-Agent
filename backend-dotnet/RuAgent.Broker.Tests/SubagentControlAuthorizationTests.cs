using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SubagentControlAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-control-auth-" + Guid.NewGuid().ToString("N"));
    private readonly BrokerSession _session;
    private readonly ToolExecutionService _tools;
    private BrokerExecutionRuntime _runtime;

    public SubagentControlAuthorizationTests()
    {
        Directory.CreateDirectory(_root);
        _session = BrokerSession.CreateForHost(_root, DateTimeOffset.UtcNow.AddMinutes(30));
        _session.ConfigureAdvertisedCapabilities(["agent_spawn", "agent_send", "agent_wait", "agent_cancel"]);
        _tools = new(_session, _root);
        // Explicit subset exercises the same run-bound controls as the full catalog.
        _runtime = new(_session, _tools, advertisedCapabilities: ["agent_spawn", "agent_send", "agent_wait", "agent_cancel"]);
    }

    [Theory]
    [InlineData("agent_spawn", "{\"role\":\"review\",\"task\":\"Inspect parser\"}")]
    [InlineData("agent_send", "{\"agent_id\":\"agent_one\",\"message\":\"Inspect empty input\"}")]
    [InlineData("agent_wait", "{\"agent_ids\":[\"agent_one\"],\"timeout_ms\":0}")]
    [InlineData("agent_cancel", "{\"agent_id\":\"agent_one\"}")]
    public async Task Only_one_exact_native_call_can_enter_the_host_dispatch_callback(string tool, string json)
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var args = JsonNode.Parse(json)!.AsObject();
        var evaluation = await _runtime.EvaluateAsync(Request(tool, args), default);
        Assert.Equal("allow", evaluation.Decision!.Decision);
        var request = Request(tool, args, evaluation.Decision.AuthorizationToken);
        request.Payload["tool_call_id"] = "call_forged";
        var callbacks = 0;
        Task<int> Dispatch(AuthorizedSubagentControl intent, CancellationToken ct)
        {
            intent.DemandActive();
            Assert.Equal("run_parent", intent.RootRunId);
            Assert.Equal("parent", intent.SessionId);
            Assert.Equal(tool, intent.ToolId);
            Assert.Equal("call_original", intent.InvocationId);
            callbacks++;
            return Task.FromResult(7);
        }
        var stale = await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.AuthorizeSubagentControlAsync(request, Dispatch, default));
        Assert.Equal("decision_stale", stale.Code);
        Assert.Equal(0, callbacks);
        request.Payload["tool_call_id"] = "call_original";
        Assert.Equal(7, await _runtime.AuthorizeSubagentControlAsync(request, Dispatch, default));
        await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.AuthorizeSubagentControlAsync(request, Dispatch, default));
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task Authorized_intent_cannot_be_retained_and_used_after_dispatch_returns()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var args = new JsonObject { ["role"] = "review", ["task"] = "Inspect parser" };
        var decision = await _runtime.EvaluateAsync(Request("agent_spawn", args), default);
        AuthorizedSubagentControl? captured = null;
        await _runtime.AuthorizeSubagentControlAsync(Request("agent_spawn", args, decision.Decision!.AuthorizationToken),
            (intent, _) => { captured = intent; intent.DemandActive(); return Task.FromResult(0); }, default);
        var stale = Assert.Throws<AgentProtocolException>(() => captured!.DemandActive());
        Assert.Equal("subagent_control_stale", stale.Code);
    }

    [Fact]
    public async Task Parent_stop_cancels_callback_and_invalidates_its_authorized_intent()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var args = new JsonObject { ["role"] = "review", ["task"] = "Inspect parser" };
        var decision = await _runtime.EvaluateAsync(Request("agent_spawn", args), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runtime.AuthorizeSubagentControlAsync(Request("agent_spawn", args, decision.Decision!.AuthorizationToken),
            (intent, ct) =>
            {
                _runtime.CancelRun("run_parent");
                Assert.True(ct.IsCancellationRequested);
                Assert.ThrowsAny<OperationCanceledException>(() => intent.DemandActive());
                return Task.FromResult(0);
            }, default));
    }

    [Fact]
    public async Task Plan_review_is_allowed_but_coding_and_unknown_properties_fail_closed()
    {
        await Open(EnvelopeTestSnapshots.Snapshot(1, "plan", []));
        var args = new JsonObject { ["role"] = "review", ["task"] = "Inspect parser" };
        Assert.Equal("allow", (await _runtime.EvaluateAsync(Request("agent_spawn", args), default)).Decision!.Decision);
        args["role"] = "coding";
        Assert.Equal("deny", (await _runtime.EvaluateAsync(Request("agent_spawn", args), default)).Decision!.Decision);
        args["role"] = "review";
        args["trusted"] = true;
        Assert.Equal("deny", (await _runtime.EvaluateAsync(Request("agent_spawn", args), default)).Decision!.Decision);
    }

    [Fact]
    public async Task Missing_capability_does_not_authorize_or_dispatch_controls()
    {
        _runtime.Dispose();
        _session.ConfigureAdvertisedCapabilities(BrokerPolicyContracts.AuthoritativeFallbackCapabilities()
            .Where(id => !id.StartsWith("agent_", StringComparison.Ordinal)).ToArray());
        _runtime = new(_session, _tools, advertisedCapabilities: _session.AdvertisedCapabilities);
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var args = new JsonObject { ["role"] = "review", ["task"] = "Inspect parser" };
        var evaluation = await _runtime.EvaluateAsync(Request("agent_spawn", args), default);
        Assert.Equal("phase_not_available", evaluation.Error!.ErrorCode);
        var calls = 0;
        await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.AuthorizeSubagentControlAsync(
            Request("agent_spawn", args, "forged"), (_, _) => { calls++; return Task.FromResult(0); }, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Closed_lease_token_cannot_authorize_controls_in_a_new_generation()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var args = new JsonObject { ["role"] = "review", ["task"] = "Inspect parser" };
        var decision = (await _runtime.EvaluateAsync(Request("agent_spawn", args), default)).Decision!;
        await _runtime.CloseSessionAsync("parent", default);
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var calls = 0;
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => _runtime.AuthorizeSubagentControlAsync(
            Request("agent_spawn", args, decision.AuthorizationToken), (_, _) => { calls++; return Task.FromResult(0); }, default));
        Assert.Equal("decision_stale", error.Code);
        Assert.Equal(0, calls);

        var fresh = (await _runtime.EvaluateAsync(Request("agent_spawn", args), default)).Decision!;
        await _runtime.AuthorizeSubagentControlAsync(Request("agent_spawn", args, fresh.AuthorizationToken),
            (_, _) => { calls++; return Task.FromResult(0); }, default);
        Assert.Equal(1, calls);
    }

    private async Task Open(BrokerPolicySnapshotWire policy)
    {
        var opened = await _runtime.OpenRunAsync(new(2, "open", AgentMessageTypes.RunOpen, "parent", null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", _root, policy)), default);
        Assert.Null(opened.Error);
    }

    private static AgentEnvelope Request(string tool, JsonObject arguments, string? token = null) =>
        new(2, Guid.NewGuid().ToString("N"), token is null ? AgentMessageTypes.ToolEvaluate : AgentMessageTypes.ToolExecute,
            "parent", null, 10000, new JsonObject
            {
                ["run_id"] = "run_parent", ["tool_id"] = tool, ["tool_version"] = "1.0.0",
                ["tool_call_id"] = "call_original", ["arguments"] = arguments.DeepClone(), ["authorization_token"] = token,
            });

    public void Dispose()
    {
        _runtime.Dispose();
        _tools.Dispose();
        Directory.Delete(_root, true);
    }
}
