using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class ContextCompactionCoordinatorTests
{
    [Fact]
    public async Task Checkpoint_retains_only_schemas_referenced_by_selected_native_history()
    {
        var store = new InMemoryHarnessEventStore(); store.Seed(Run());
        var model = new ChunkedCheckpointModel();
        const string schema = "{}";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schema))).ToLowerInvariant();
        var read = new PromptCapability("read_file", "1.0.0", "Read a file.", "workspace_read", schema, digest, null, "enabled");
        var write = new PromptCapability("write_file", "1.0.0", "Write a file.", "workspace_write", schema, digest, null, "enabled");
        var prompt = new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion, SystemPromptContext.RequiredAgentProfile,
            "standard", SystemPromptContext.Empty.Environment, [], [read, write], new PromptRuntimeContext([]));
        using var arguments = JsonDocument.Parse("{}");
        var call = new ModelToolCall("provider-read", read.Id, read.Version, arguments.RootElement);
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_" + new string('a', 32)),
            new ToolCallId("call_" + new string('a', 32)), ToolExecutionOutcome.Succeeded, "ok", null, false,
            providerCallId: call.ProviderCallId);
        var history = Snapshot(
            ModelContextEntry.Assistant("old-call", 1, new AssistantStep(new string('x', 20000), [call], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("old-result", 2, receipt, "contents"),
            ModelContextEntry.Text("current", 3, ModelContextRole.User, "continue"));
        var coordinator = new AgentRunCoordinator(model, new FakeToolGateway(), store,
            new AgentContextAssembler(new ReloadingSource(history, Snapshot())),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(4000), promptContextProvider: new FakeSystemPromptContextProvider((_, _) => prompt));

        var result = await coordinator.CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);

        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(read, Assert.Single(Assert.Single(model.Prompts).Capabilities));
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events), evt => evt.Kind == HarnessEventKind.ToolExecutionRequested);
    }
}
