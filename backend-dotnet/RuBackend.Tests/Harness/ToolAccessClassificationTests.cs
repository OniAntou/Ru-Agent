using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Hooks;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The Broker gateway and the hook gateway share one access classification. Each case below is a tool
/// the two used to classify differently, or an edge the classification must keep. Every case is
/// asserted through both callers: the Broker's AccessKindForTool and a real PreToolUse block, whose
/// deny decision carries the access class.
/// </summary>
public sealed class ToolAccessClassificationTests
{
    private static readonly RunId Run = new("run_" + new string('b', 32));

    // The ten tools the Broker and hook gateways used to disagree on. Each chosen class is the more
    // conservative of the two former values (Terminal > Mutation > ReadOnly); the Broker value was
    // already that class for every row, so only the hook side moved.
    [Theory]
    [InlineData("todo_write", "{}", ToolAccessKind.Mutation)]
    [InlineData("agent_spawn", "{}", ToolAccessKind.Mutation)]
    [InlineData("agent_send", "{}", ToolAccessKind.Mutation)]
    [InlineData("agent_cancel", "{}", ToolAccessKind.Mutation)]
    [InlineData("agent_apply", "{}", ToolAccessKind.Mutation)]
    [InlineData("terminal_stop", "{}", ToolAccessKind.Terminal)]
    [InlineData("mcp", "{}", ToolAccessKind.Terminal)]
    [InlineData("run_code", "{}", ToolAccessKind.Terminal)]
    [InlineData("computer", "{\"action\":\"click\"}", ToolAccessKind.Terminal)]
    [InlineData("ask_user", "{}", ToolAccessKind.Terminal)]
    // code_intelligence is not one of the disagreeing ten: the hook side's default would have said ReadOnly.
    [InlineData("code_intelligence", "{}", ToolAccessKind.Terminal)]
    public async Task Disagreeing_tool_gets_the_chosen_class_through_both_gateways(string toolId, string argumentsJson, ToolAccessKind expected)
    {
        Assert.Equal(expected, BrokerGatewayContracts.AccessKindForTool(toolId, ParseObject(argumentsJson)));

        var decision = await DenyByPreToolHookAsync(toolId, argumentsJson);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal(expected, decision.AccessKind);
    }

    // Edges that keep their class: observation stays read-only and the read-only and mutation tools
    // are unchanged on both sides. The unknown-tool probe is the one hook-side move this file pins:
    // the hook gateway used to say ReadOnly for any unknown id, and now fails closed to Terminal.
    [Theory]
    [InlineData("computer", "{\"action\":\"screenshot\"}", ToolAccessKind.ReadOnly)]
    [InlineData("computer", "{\"action\":\"list\"}", ToolAccessKind.ReadOnly)]
    [InlineData("future_tool", "{}", ToolAccessKind.Terminal)]
    [InlineData("agent_wait", "{}", ToolAccessKind.ReadOnly)]
    [InlineData("read_file", "{}", ToolAccessKind.ReadOnly)]
    [InlineData("terminal_read", "{}", ToolAccessKind.ReadOnly)]
    [InlineData("write_file", "{}", ToolAccessKind.Mutation)]
    [InlineData("powershell", "{}", ToolAccessKind.Terminal)]
    public async Task Unchanged_edges_keep_their_class_through_both_gateways(string toolId, string argumentsJson, ToolAccessKind expected)
    {
        Assert.Equal(expected, BrokerGatewayContracts.AccessKindForTool(toolId, ParseObject(argumentsJson)));

        var decision = await DenyByPreToolHookAsync(toolId, argumentsJson);

        Assert.Equal(expected, decision.AccessKind);
    }

    private static JsonObject? ParseObject(string json) => JsonNode.Parse(json) as JsonObject;

    private static Task<ToolProposalDecision> DenyByPreToolHookAsync(string toolId, string argumentsJson)
    {
        var root = Directory.CreateTempSubdirectory("ru-access-class").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".ru"));
        File.WriteAllText(Path.Combine(root, ".ru", "hooks.json"), """{"hooks":{"PreToolUse":[{"command":"x"}]}}""");
        var gateway = new HookToolGateway(
            new ThrowingGateway(),
            HookConfiguration.Load(root, null),
            new BlockingRunner(),
            root);
        var proposal = new ToolProposal(
            Run,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('c', 32)),
            "provider_call_1",
            1,
            toolId,
            "1.0.0",
            JsonSerializer.SerializeToElement(JsonNode.Parse(argumentsJson)));
        return gateway.EvaluateAsync(proposal, CancellationToken.None);
    }

    private sealed class BlockingRunner : IHookRunner
    {
        public Task<HookResult> RunAsync(HookDefinition hook, string stdinJson, string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(new HookResult(2, "blocked by test hook", false));
    }

    private sealed class ThrowingGateway : IToolGateway
    {
        public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a blocked call must not reach the inner gateway");

        public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ToolExecutionReceipt> ExecuteAsync(
            PreparedToolExecution execution, Func<ToolOutputChunk, ValueTask> publishDelta, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
