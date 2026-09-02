using System.Text.Json;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HelpDocsGatewayTests
{
    [Theory]
    [InlineData(AgentPermissionPreset.Standard)]
    [InlineData(AgentPermissionPreset.FullAccess)]
    public async Task Bundled_help_is_read_only_workspace_independent_and_bounded(AgentPermissionPreset preset)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-help-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var gateway = new HarnessToolGateway(root, new[] { "help.docs" }, _ => preset, new SimpleIdGenerator());
            var proposal = Proposal("{\"query\":\"approval\",\"max_results\":1}");
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);

            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            Assert.Equal(ToolAccessKind.ReadOnly, decision.AccessKind);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(proposal, new ExecutionId("exec_" + new string('a', 32)), decision.CanonicalIntentToken!),
                CancellationToken.None);
            Assert.Empty(prepared.PreparationPayloads);
            var receipt = await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
            using var output = JsonDocument.Parse(receipt.ModelContent);
            Assert.Equal("approval", output.RootElement.GetProperty("query").GetString());
            Assert.True(output.RootElement.GetProperty("results").GetArrayLength() <= 1);
            Assert.Contains("agent-tools", Encoding.UTF8.GetString(receipt.ModelContent.Span), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Invalid_help_arguments_are_rejected_before_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-help-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var gateway = new HarnessToolGateway(root, new[] { "help.docs" });
            var decision = await gateway.EvaluateAsync(Proposal("{\"query\":\"x\",\"path\":\"README.md\"}"), CancellationToken.None);

            Assert.Equal(PolicyDecision.Deny, decision.Decision);
            Assert.Equal("invalid_arguments", decision.SafeCode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ToolProposal Proposal(string arguments) => new(
        new RunId("run_" + new string('a', 32)),
        new ModelStepId("step_" + new string('a', 32)),
        new ToolCallId("call_" + new string('a', 32)),
        1,
        "help.docs",
        "1.0.0",
        JsonDocument.Parse(arguments).RootElement);

    private sealed class SimpleIdGenerator : IIdGenerator
    {
        private int _counter;
        private string Next(string prefix) => prefix + (++_counter).ToString("x32");
        public RunId NewRunId() => new(Next("run_"));
        public ModelStepId NewModelStepId() => new(Next("step_"));
        public ToolCallId NewToolCallId() => new(Next("call_"));
        public ExecutionId NewExecutionId() => new(Next("exec_"));
        public ApprovalId NewApprovalId() => new(Next("apr_"));
        public TrustRequestId NewTrustRequestId() => new(Next("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Next("rule_"));
        public EventId NewEventId() => new(Next("evt_"));
        public PayloadId NewPayloadId() => new(Next("payload_"));
    }
}
