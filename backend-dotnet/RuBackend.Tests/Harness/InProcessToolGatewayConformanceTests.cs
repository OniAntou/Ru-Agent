using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Tests.Conformance;
using RuAgent.Harness.Core.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Conformance suite over the in-process execution authority: a descriptor-free
/// session, a real tool service over the base suite's workspace, and the
/// transport-neutral runtime, all inside the test process. No process-global
/// environment mutation and no loopback sockets. Every case still spawns real
/// terminal processes for the bash cases, so it shares the non-parallel
/// process-heavy collection.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class InProcessToolGatewayConformanceTests : ToolGatewayConformance
{
    private readonly List<InProcessExecutionFixture> _fixtures = [];

    protected override bool StreamsToolOutputDeltas => false;

    [Fact]
    public async Task Failed_terminal_command_delivers_diagnostics_to_the_model()
    {
        var gateway = await CreatePresetGatewayAsync(FullAccessPreset);
        var receipt = await ExecuteOnAsync(gateway, "bash", new JsonObject
        {
            ["mode"] = "shell",
            ["script"] = "[Console]::Error.WriteLine('compiler: missing semicolon'); exit 1",
            ["working_directory"] = ".",
        });

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("process_exit_nonzero", receipt.Code);
        var content = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(receipt.ModelContent.Span))!;
        Assert.Equal(1, content["exit_code"]!.GetValue<int>());
        Assert.Contains("compiler: missing semicolon", content["stderr_head"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(content["spool_id"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("web_search", ToolAccessKind.ReadOnly)]
    [InlineData("web_fetch", ToolAccessKind.ReadOnly)]
    [InlineData("todo_write", ToolAccessKind.Mutation)]
    [InlineData("future_tool", ToolAccessKind.Terminal)]
    public void Gateway_access_classification_is_conservative(
        string toolId,
        ToolAccessKind expected)
    {
        var actual = global::RuBackend.Harness.Broker.BrokerGatewayContracts.AccessKindForTool(
            toolId,
            null);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Gateway_multiplexes_independent_evaluations_and_issues_distinct_tokens()
    {
        var fixture = new InProcessExecutionFixture();
        _fixtures.Add(fixture);
        var runId = new RunId("run_" + new string('a', 31) + "1");
        const string sessionId = "session-concurrency";
        var gateway = await fixture.Factory.CreateAsync(
            fixture.WorkspaceRoot,
            Snapshot(StandardPreset),
            runId,
            sessionId,
            CancellationToken.None);
        var first = gateway.EvaluateAsync(
            ListProposal(runId, "provider_call_1"),
            CancellationToken.None);
        var second = gateway.EvaluateAsync(
            ListProposal(runId, "provider_call_2"),
            CancellationToken.None);

        var decisions = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(decisions, decision => Assert.Equal(PolicyDecision.Allow, decision.Decision));
        Assert.False(string.IsNullOrWhiteSpace(decisions[0].CanonicalIntentToken));
        Assert.False(string.IsNullOrWhiteSpace(decisions[1].CanonicalIntentToken));
        Assert.NotEqual(decisions[0].CanonicalIntentToken, decisions[1].CanonicalIntentToken);
    }

    protected override Task<IToolGateway> CreateGatewayAsync(string workspaceRoot, string preset)
    {
        var fixture = new InProcessExecutionFixture(workspaceRoot);
        _fixtures.Add(fixture);
        // The shared suite builds proposals with a fixed run id; the factory
        // opens its lease for that same run so approval state keys line up.
        var runId = new RunId("run_" + new string('0', 32));
        return fixture.Factory.CreateAsync(
            workspaceRoot,
            Snapshot(preset),
            runId,
            agentSessionId: "session-conformance",
            CancellationToken.None);
    }

    private static ToolProposal ListProposal(RunId runId, string providerCallId) =>
        new(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 31) + (providerCallId.EndsWith('1') ? '1' : '2')),
            providerCallId,
            modelOrder: 1,
            "list",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject { ["path"] = "." }));

    protected override void DisposeGateway()
    {
        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }

        _fixtures.Clear();
    }

    /// <summary>
    /// The same Host-side snapshot the WS subclass projects by hand: version 1,
    /// the requested preset, the conformance workspace identity, no rules, and
    /// the fixed creation timestamp.
    /// </summary>
    private static PolicySnapshot Snapshot(string preset) =>
        PolicySnapshotCanonicalizer.Create(
            1,
            string.Equals(preset, FullAccessPreset, StringComparison.Ordinal)
                ? AgentPermissionPreset.FullAccess
                : AgentPermissionPreset.Standard,
            new WorkspaceIdentity(
                new WorkspaceId("ws_" + new string('1', 32)),
                "VOL-CONFORMANCE",
                "fid-conformance",
                new string('a', 64)),
            [],
            DateTimeOffset.Parse(
                "2026-08-24T12:00:00.0000000+00:00",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
}
