using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core.Tests;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class AgentPromptReceiptTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_is_called_for_each_step_and_changed_instruction_digest_is_observed()
    {
        var contents = new Queue<string>(["first guidance", "second guidance"]);
        var provider = new FakeSystemPromptContextProvider((_, _) => Context(contents.Dequeue()));
        var model = new FakeModelStepRunner();
        model.Enqueue(ToolStep());
        model.Enqueue(new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = CreateCoordinator(model, store, provider);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal(2, provider.Contexts.Count);
        Assert.Equal(2, model.PromptContexts.Count);
        Assert.NotEqual(
            provider.Contexts[0].InstructionBlocks[0].ContentSha256,
            provider.Contexts[1].InstructionBlocks[0].ContentSha256);
        Assert.Equal(
            provider.Contexts.Select(item => item.InstructionBlocks[0].ContentSha256),
            model.PromptContexts.Select(item => item.InstructionBlocks[0].ContentSha256));
    }

    [Fact]
    public async Task Invalid_receipt_fails_run_before_completion_history_or_tool_dispatch()
    {
        var context = Context("valid guidance");
        var provider = new FakeSystemPromptContextProvider((_, _) => context);
        var model = new FakeModelStepRunner();
        model.Enqueue(new ModelStepResult(
            new AssistantStep(
                "should not commit",
                Array.Empty<ModelToolCall>(),
                AssistantFinishReason.Stop,
                0),
            Receipt(context, instructionDigest: new string('e', 64))));
        var gateway = new FakeToolGateway();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = CreateCoordinator(model, store, provider, gateway);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        Assert.Equal("prompt_contract_invalid", error.Message);
        var run = await store.GetRunAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Failed, run!.State);
        var events = store.Commits.SelectMany(commit => commit.Events).ToArray();
        Assert.Contains(events, item =>
            item.Kind == HarnessEventKind.RunFailed
            && item.SafeCode == "prompt_contract_invalid");
        Assert.DoesNotContain(events, item => item.Kind == HarnessEventKind.ModelStepCompleted);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Payloads),
            payload => payload.Kind is HarnessPayloadKind.AssistantText or HarnessPayloadKind.AssistantStep);
        Assert.Empty(gateway.Proposals);
    }

    [Fact]
    public async Task Context_provider_failure_fails_run_without_calling_model()
    {
        var provider = new FakeSystemPromptContextProvider((_, _) =>
            throw new InvalidOperationException("loader failure"));
        var model = new FakeModelStepRunner();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = CreateCoordinator(model, store, provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunOneStepAsync(RunId(), CancellationToken.None));

        Assert.Equal("prompt_contract_invalid", error.Message);
        Assert.Empty(model.Requests);
        var run = await store.GetRunAsync(RunId(), CancellationToken.None);
        Assert.Equal(HarnessRunState.Failed, run!.State);
        Assert.Contains(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.Kind == HarnessEventKind.RunFailed
                && item.SafeCode == "prompt_contract_invalid");
    }

    private static AgentRunCoordinator CreateCoordinator(
        FakeModelStepRunner model,
        InMemoryHarnessEventStore store,
        FakeSystemPromptContextProvider provider,
        FakeToolGateway? gateway = null) =>
        new(
            model,
            gateway ?? new FakeToolGateway(),
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new CryptographicIdGenerator(),
            new SystemTimeProvider(),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            promptContextProvider: provider);

    private static SystemPromptContext Context(string content)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
        return new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            "ru-agent",
            "standard",
            new PromptEnvironment(
                "test-model",
                "windows",
                "vi-VN",
                "Asia/Bangkok",
                "2026-08-29",
                "LLM"),
            [new PromptInstructionBlock("root:Ru.md", "Ru.md", content, digest)],
            [new PromptCapability(
                "files.read",
                "1.0.0",
                "Read a file",
                "workspace.read",
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}",
                new string('a', 64),
                null,
                "enabled")],
            new PromptRuntimeContext(
                null,
                null,
                Array.Empty<PromptReference>()));
    }

    private static SystemPromptReceipt Receipt(
        SystemPromptContext context,
        string? instructionDigest = null)
    {
        var zero = new string('0', 64);
        return new SystemPromptReceipt(
            SystemPromptReceipt.SupportedComposerVersion,
            zero,
            [
                new PromptBlockDigest("ru.persona", zero),
                new PromptBlockDigest("agent.contract", zero),
                new PromptBlockDigest("environment", zero),
                new PromptBlockDigest("profile", zero),
                new PromptBlockDigest(
                    "instruction.root:Ru.md",
                    instructionDigest ?? context.InstructionBlocks[0].ContentSha256),
                new PromptBlockDigest("capabilities", zero),
                new PromptBlockDigest("runtime", zero),
            ]);
    }

    private static AssistantStep ToolStep() =>
        new(
            "checking",
            [new ModelToolCall(
                "provider-call",
                "files.read",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\"README.md\"}").RootElement.Clone())],
            AssistantFinishReason.ToolCalls,
            0);

    private static AgentRun Run() =>
        new(
            RunId(),
            new SessionId(new string('1', 32)),
            new WorkspaceId("ws_" + new string('2', 32)),
            new HarnessRunStatus(HarnessRunState.Running),
            version: 0,
            lastSequence: 0,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('a', 64),
            createdAt: Epoch,
            updatedAt: Epoch);

    private static RunId RunId() => new("run_" + new string('3', 32));

    private sealed class EmptyContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                Array.Empty<ModelContextEntry>()));
    }
}
