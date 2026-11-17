using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>Native continuation of the preceding root run under the same model (ADR 0049).</summary>
public sealed partial class HarnessModelContextBootstrapperTests
{
    private static readonly AgentRunProviderSelection ModelA = new("Custom OpenAI", "custom", "chat_completions",
        "https://provider.invalid/v1", "model-a", "custom_api_key", "{}", 8192);

    [Fact]
    public async Task The_same_model_reads_the_previous_run_as_native_history()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (previous, next, selections) = await SeedStoppedRunAsync(fixture, ModelA, ModelA, captureSourceBase: true);
        var bootstrapper = NewBootstrapper(fixture, previous, selections);

        await bootstrapper.CaptureAsync(next.RunId, next.SessionId, default);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore).LoadAsync(next.RunId, default);

        Assert.DoesNotContain(snapshot.SessionBase, entry => entry.SourceId == SessionRunHandoff.ContextSourceId);
        // The stopped run's own turn is carried natively, not repeated as dialogue.
        Assert.DoesNotContain(snapshot.SessionBase, entry => entry.SourceId.StartsWith("base:turn:", StringComparison.Ordinal));
        var step = Assert.Single(snapshot.SessionBase, entry => entry.Kind == ModelContextEntryKind.AssistantStep);
        Assert.Equal("old-call", Assert.Single(step.AssistantStep!.ToolCalls).ProviderCallId);
        Assert.Equal("Parser.Tests: 12 passed",
            Assert.Single(snapshot.SessionBase, entry => entry.Kind == ModelContextEntryKind.ToolResult).Content);

        var context = await new AgentContextAssembler(new SqliteModelContextSource(fixture.Options, fixture.PayloadStore))
            .AssembleAsync(next.RunId, default);
        var (current, _, messages) = RuBackend.Harness.ModelRunner.InProcessModelStepRunner.BuildConversationForTests(context);
        Assert.Equal("Check the results and continue.", current);
        Assert.Contains(messages!, message => message.ToolCalls.Any(call => call.Id == "old-call"));
        Assert.Contains(messages!, message => message.Role == "tool" && message.Content.Contains("12 passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_model_reads_the_previous_run_through_the_text_handoff()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (previous, next, selections) = await SeedStoppedRunAsync(fixture, ModelA, ModelA with { Model = "model-b" },
            captureSourceBase: true);
        var bootstrapper = NewBootstrapper(fixture, previous, selections);

        await bootstrapper.CaptureAsync(next.RunId, next.SessionId, default);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore).LoadAsync(next.RunId, default);

        Assert.DoesNotContain(snapshot.SessionBase, entry => entry.Kind == ModelContextEntryKind.ToolResult
            || entry.AssistantStep?.ToolCalls.Count > 0);
        Assert.Contains("Parser.Tests: 12 passed",
            Assert.Single(snapshot.SessionBase, entry => entry.SourceId == SessionRunHandoff.ContextSourceId).Content);
    }

    [Fact]
    public async Task A_source_that_cannot_be_projected_falls_back_to_the_text_handoff()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        // No captured base: the source cannot be loaded as a run context.
        var (previous, next, selections) = await SeedStoppedRunAsync(fixture, ModelA, ModelA, captureSourceBase: false);
        var bootstrapper = NewBootstrapper(fixture, previous, selections);

        await bootstrapper.CaptureAsync(next.RunId, next.SessionId, default);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore).LoadAsync(next.RunId, default);

        Assert.Contains(snapshot.SessionBase, entry => entry.SourceId == SessionRunHandoff.ContextSourceId);
        Assert.DoesNotContain(snapshot.SessionBase, entry => entry.AssistantStep?.ToolCalls.Count > 0);
    }

    [Fact]
    public async Task An_unresolvable_model_keeps_the_default_handoff()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var (previous, next, selections) = await SeedStoppedRunAsync(fixture, ModelA, ModelA, captureSourceBase: true);
        var facts = new AgentRunHandoffModelFacts(new FailingMetadataResolver(), selections);
        var bootstrapper = new HarnessModelContextBootstrapper(new SnapshotSessionStore(Session(previous, next)),
            fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System, facts);

        await bootstrapper.CaptureAsync(next.RunId, next.SessionId, default);
        var snapshot = await new SqliteModelContextSource(fixture.Options, fixture.PayloadStore).LoadAsync(next.RunId, default);

        Assert.Contains(snapshot.SessionBase, entry => entry.SourceId == SessionRunHandoff.ContextSourceId);
    }

    [Fact]
    public async Task A_slow_model_resolution_is_abandoned_with_the_default_handoff()
    {
        var facts = new AgentRunHandoffModelFacts(new HangingMetadataResolver(), null!);

        var started = DateTime.UtcNow;
        var read = await facts.ReadAsync(new RunId("run_" + new string('a', 32)), default);

        Assert.Null(read);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, AgentRunHandoffModelFacts.ResolveTimeout * 3);
    }

    private sealed class HangingMetadataResolver : IAgentRunModelMetadataResolver
    {
        public async Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static HarnessModelContextBootstrapper NewBootstrapper(BackendHarnessTestFixture fixture, AgentRun previous,
        SqliteAgentRunProviderSelectionStore selections) =>
        new(new SnapshotSessionStore(Session(previous, null)), fixture.PayloadStore, new CryptographicIdGenerator(),
            TimeProvider.System, new AgentRunHandoffModelFacts(new FixedMetadataResolver(), selections));

    private static SessionDocument Session(AgentRun previous, AgentRun? next) =>
        new(previous.SessionId.Value, "Continue", BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp,
        [
            new SessionMessage("user", "Fix the parser and run its tests.", "source-old-user", previous.RunId.Value),
            new SessionMessage("ai", "Stopped: tool call budget exhausted."),
        ]);

    /// <summary>A stopped run that read a file, and a new run in the same session after it.</summary>
    private static async Task<(AgentRun Previous, AgentRun Next, SqliteAgentRunProviderSelectionStore Selections)>
        SeedStoppedRunAsync(BackendHarnessTestFixture fixture, AgentRunProviderSelection previousModel,
            AgentRunProviderSelection nextModel, bool captureSourceBase)
    {
        var prompt = new HarnessPayloadIntent(new PayloadId("payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, System.Text.Encoding.UTF8.GetBytes("Fix the parser and run its tests."));
        var previous = await fixture.SeedRunAsync(payloads: [prompt]);
        if (captureSourceBase)
            await fixture.PayloadStore.CaptureBaseContextAsync(previous.Run.RunId, [], BackendHarnessTestFixture.Timestamp, default);
        var selections = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        await selections.PinAsync(previous.Run.RunId, previousModel, default);
        var step = new AssistantStep("Updated parser.cs; now checking the tests.",
            [new ModelToolCall("old-call", "read_file", "1",
                System.Text.Json.JsonDocument.Parse("{\"path\":\"src/Parser.cs\"}").RootElement)],
            AssistantFinishReason.ToolCalls, 1);
        var assistant = new HarnessPayloadIntent(new PayloadId("payload_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            HarnessPayloadKind.AssistantStep, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 8192, AssistantStepJsonCodec.Serialize(step));
        var output = new HarnessPayloadIntent(new PayloadId("payload_cccccccccccccccccccccccccccccccc"),
            HarnessPayloadKind.ToolOutput, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, System.Text.Encoding.UTF8.GetBytes("Parser.Tests: 12 passed"));
        var ended = BackendHarnessTestFixture.Timestamp.AddSeconds(3);
        var current = await fixture.EventStore.GetRunAsync(previous.Run.RunId, default)
            ?? throw new InvalidOperationException("run missing");
        var stopped = new AgentRun(previous.Run.RunId, previous.Run.SessionId, previous.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Failed), current.Version + 1, current.LastSequence + 3, 1, 1, 1,
            previous.Run.Preset, 1, previous.Run.PolicySnapshotSha256, previous.Run.CreatedAt, ended,
            terminalAt: ended, terminalErrorCode: "tool_call_budget_exhausted");
        await fixture.EventStore.CommitAsync(new HarnessCommit(previous.Run.RunId, current.Version, stopped,
            [new HarnessEventIntent(new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                HarnessEventKind.ModelStepCompleted, payloadId: assistant.PayloadId),
             new HarnessEventIntent(new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                HarnessEventKind.ToolExecutionCompleted,
                toolCallId: new ToolCallId("call_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), providerCallId: "old-call",
                executionId: new ExecutionId("exec_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                payloadId: output.PayloadId, safeCode: "ok", toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false),
             new HarnessEventIntent(new EventId("evt_cccccccccccccccccccccccccccccccc"), HarnessEventKind.RunStateChanged)],
            [assistant, output], []), default);

        var next = new AgentRun(new RunId("run_22222222222222222222222222222222"), previous.Run.SessionId,
            previous.Run.WorkspaceId, new HarnessRunStatus(HarnessRunState.Created), 0, 1, 1, 0, 0,
            previous.Run.Preset, 1, previous.Run.PolicySnapshotSha256, ended.AddSeconds(1), ended.AddSeconds(1));
        var nextPrompt = new HarnessPayloadIntent(new PayloadId("payload_dddddddddddddddddddddddddddddddd"),
            HarnessPayloadKind.UserPrompt, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 4096, System.Text.Encoding.UTF8.GetBytes("Check the results and continue."));
        await fixture.EventStore.TryCreateRunAsync(new NewAgentRun(next,
            [new HarnessEventIntent(new EventId("evt_dddddddddddddddddddddddddddddddd"), HarnessEventKind.RunStateChanged,
                payloadId: nextPrompt.PayloadId)],
            [nextPrompt], [], new RunCreationIdentity(new string('e', 64), new string('f', 64), 1)), default);
        await selections.PinAsync(next.RunId, nextModel, default);
        return (stopped, next, selections);
    }

    private sealed class FixedMetadataResolver : IAgentRunModelMetadataResolver
    {
        public Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRunModelMetadata("model-a", "chat_completions", 200_000, "provider_catalog",
                "catalog-v1", 8192));
    }

    private sealed class FailingMetadataResolver : IAgentRunModelMetadataResolver
    {
        public Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken) =>
            throw new ProviderCatalogException("provider_selection_missing");
    }
}
