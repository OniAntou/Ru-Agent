using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentProviderTransientParkTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(ConsecutiveToolCallGuard.MaximumRepairNotes)]
    public async Task Suppressed_repeated_call_can_recover_through_durable_native_history(int priorNotes)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.Running));
        var ids = new CryptographicIdGenerator();
        var baseEntries = new List<HarnessBaseContextEntry>
        {
            new(ids.NewPayloadId(), "prompt", 1, ModelContextRole.User, "Repair the invalid tool arguments."),
        };
        for (var index = 0; index < priorNotes; index++)
        {
            baseEntries.Add(new HarnessBaseContextEntry(ids.NewPayloadId(), $"note-{index}", index + 2,
                ModelContextRole.System, ConsecutiveToolCallGuard.BuildRepairNote("malformed_call")));
        }
        await fixture.PayloadStore.CaptureBaseContextAsync(seeded.Run.RunId, baseEntries,
            BackendHarnessTestFixture.Timestamp, CancellationToken.None);
        var gateway = new InvalidArgumentsGateway();
        ModelRequest? recoveredRequest = null;
        var provider = new ScriptedProvider((request, call) =>
        {
            if (call <= 2)
            {
                return Deltas(new ModelDelta(null,
                    [new ModelToolFragment($"provider-{call}", "read", "{\"path\":\"a.cs\"}")],
                    completed: true, finishReason: "tool_calls"));
            }
            recoveredRequest = request;
            return Deltas(new ModelDelta("I need to repair the arguments.", [],
                completed: true, finishReason: "stop"));
        });
        var coordinator = Coordinator(fixture, provider, new RecordingActivitySink(),
            new SqliteModelContextSource(fixture.Options, fixture.PayloadStore), gateway, new ReadPromptContextProvider());

        await coordinator.RunOneStepAsync(seeded.Run.RunId, CancellationToken.None);
        var suppressed = await coordinator.RunOneStepAsync(seeded.Run.RunId, CancellationToken.None);
        Assert.Equal(1, gateway.Evaluations);
        if (priorNotes == ConsecutiveToolCallGuard.MaximumRepairNotes)
        {
            Assert.Equal(HarnessRunState.WaitingUser, suppressed.Run.State);
            await coordinator.ContinueAsync(seeded.Run.RunId, CancellationToken.None);
        }
        else
        {
            Assert.Equal(HarnessRunState.Running, suppressed.Run.State);
        }

        // Actual SQLite context assembly and native conversation projection must
        // reach the provider again without an orphan call from the suppressed step.
        var recovered = await coordinator.RunOneStepAsync(seeded.Run.RunId, CancellationToken.None);
        // The failed receipt must still prevent a prose-only claim of success.
        Assert.Equal(HarnessRunState.Running, recovered.Run.State);
        Assert.Equal(3, provider.Calls);
        Assert.NotNull(recoveredRequest);
        Assert.DoesNotContain(recoveredRequest.Turns.SelectMany(turn => turn.ToolCalls),
            call => call.Id == "provider-2");
        Assert.Contains(recoveredRequest.Turns, turn =>
            turn.Role == "tool" && turn.ToolCallId == "provider-1");
        Assert.Equal(1, gateway.Evaluations);
    }

    private sealed class ReadPromptContextProvider : ISystemPromptContextProvider
    {
        public Task<SystemPromptContext> BuildAsync(AgentRun run, ModelContextSnapshot modelContext,
            CancellationToken cancellationToken)
        {
            var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;
            var capability = new PromptCapability(definition.ToolId, definition.ToolVersion,
                definition.Description, definition.Scope,
                AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
                AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                definition.ExampleArguments?.ToJsonString(), "enabled");
            return Task.FromResult(new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion,
                "ru-agent", "standard",
                new PromptEnvironment("model-b", "windows", "vi-VN", "Asia/Bangkok", "2026-10-11", "test"),
                [], [capability], new PromptRuntimeContext([])));
        }
    }

    private sealed class InvalidArgumentsGateway : IToolGateway
    {
        public int Evaluations { get; private set; }

        public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken)
        {
            Evaluations++;
            return Task.FromResult(ToolProposalDecision.Deny(ToolAccessKind.ReadOnly,
                "malformed_call", "Use valid arguments."));
        }

        public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution,
            CancellationToken cancellationToken) => throw new InvalidOperationException("denied_call_dispatched");

        public Task<ToolExecutionReceipt> ExecuteAsync(PreparedToolExecution execution,
            Func<ToolOutputChunk, ValueTask> publishDelta, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("denied_call_dispatched");

        public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
