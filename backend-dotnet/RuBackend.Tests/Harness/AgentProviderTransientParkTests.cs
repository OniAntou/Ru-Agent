using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Harness.ModelRunner;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// End-to-end drive of the real in-process runner against a scripted provider:
/// exhausted provider-transient failures must park the run for Continue, and an
/// explicit Continue must drive a fresh model step that can complete.
/// </summary>
public sealed partial class AgentProviderTransientParkTests
{
    [Fact]
    public async Task Exhausted_provider_transient_parks_the_run_and_continue_retries_a_fresh_step()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var seeded = await fixture.SeedRunAsync(
                new HarnessRunStatus(HarnessRunState.Running));
            var runId = seeded.Run.RunId;
            var provider = new ScriptedProvider((_, call) => call <= 5
                ? Throws(new ModelProviderException("rate_limited", retryable: true))
                : Deltas(new ModelDelta("done", [], completed: true, finishReason: "stop")));
            var activities = new RecordingActivitySink();
            var coordinator = Coordinator(fixture, provider, activities);

            var parked = await coordinator.RunOneStepAsync(runId, CancellationToken.None);

            Assert.Equal(HarnessRunState.WaitingUser, parked.Run.State);
            Assert.Equal("provider_rate_limited", parked.ErrorCode);
            Assert.Null(parked.Run.TerminalErrorCode);
            Assert.Equal(5, provider.Calls);
            var parkedEvents = await ReadEventsAsync(fixture, runId);
            Assert.Contains(parkedEvents, item =>
                item.Kind == "run.state_changed" && item.SafeCode == "waiting_user");
            Assert.Contains(parkedEvents, item =>
                item.Kind == "continue.requested" && item.SafeCode == "provider_rate_limited");
            Assert.DoesNotContain(parkedEvents, item => item.Kind == "run.failed");
            Assert.Contains(activities.Activities, activity =>
                activity.Kind == AgentActivityKind.Status
                && activity.State == AgentActivityState.Waiting
                && activity.SafeCode == "provider_rate_limited");

            var continued = await coordinator.ContinueAsync(runId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Running, continued.State);

            var completed = await coordinator.RunOneStepAsync(runId, CancellationToken.None);

            Assert.Null(completed.ErrorCode);
            Assert.Equal(HarnessRunState.WaitingHistory, completed.Run.State);
            Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, completed.Run.HistoryWaitKind);
            Assert.Equal(6, provider.Calls);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Provider_auth_failure_fails_immediately_without_parking()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var seeded = await fixture.SeedRunAsync(
                new HarnessRunStatus(HarnessRunState.Running));
            var runId = seeded.Run.RunId;
            var provider = new ScriptedProvider((_, _) =>
                Throws(new ModelProviderException("auth_failed", retryable: false)));
            var coordinator = Coordinator(fixture, provider, new RecordingActivitySink());

            var error = await Assert.ThrowsAsync<ModelRunnerProtocolException>(() =>
                coordinator.RunOneStepAsync(runId, CancellationToken.None));

            Assert.Equal("provider_auth", error.Code);
            Assert.Equal(1, provider.Calls);
            var run = await fixture.EventStore.GetRunAsync(runId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, run!.State);
            Assert.Equal("provider_auth", run.TerminalErrorCode);
            var events = await ReadEventsAsync(fixture, runId);
            Assert.Contains(events, item =>
                item.Kind == "run.failed" && item.SafeCode == "provider_auth");
            Assert.DoesNotContain(events, item => item.Kind == "continue.requested");
            Assert.DoesNotContain(events, item =>
                item.Kind == "run.state_changed" && item.SafeCode == "waiting_user");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static AgentRunCoordinator Coordinator(
        BackendHarnessTestFixture fixture,
        ScriptedProvider provider,
        RecordingActivitySink activities,
        IModelContextSource? contextSource = null,
        IToolGateway? gateway = null,
        ISystemPromptContextProvider? promptContextProvider = null)
    {
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            StepRetryBackoffsOverride = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
        };
        return new AgentRunCoordinator(
            runner,
            gateway ?? new UnusedToolGateway(),
            fixture.EventStore,
            new AgentContextAssembler(contextSource ?? new StaticContextSource()),
            new CryptographicIdGenerator(),
            new SystemTimeProvider(),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            activitySink: activities,
            promptContextProvider: promptContextProvider ?? new EmptyPromptContextProvider());
    }

    private static async Task<List<HarnessEvent>> ReadEventsAsync(
        BackendHarnessTestFixture fixture,
        RunId runId)
    {
        var events = new List<HarnessEvent>();
        await foreach (var envelope in fixture.EventStore
            .ReadEventsAsync(runId, 0, CancellationToken.None))
        {
            events.Add(envelope.Event);
        }

        return events;
    }

    private static ProviderBootstrapData Bootstrap() => new(
        "Kilo",
        "chat_completions",
        new Uri("https://step-provider.invalid/v1"),
        "model-b",
        "step-secret");

    private static async IAsyncEnumerable<ModelDelta> Deltas(params ModelDelta[] deltas)
    {
        await Task.Yield();
        foreach (var delta in deltas)
        {
            yield return delta;
        }
    }

    private static async IAsyncEnumerable<ModelDelta> Throws(ModelProviderException? error)
    {
        await Task.Yield();
        if (error is null)
        {
            yield break;
        }

        throw error;
    }

    private sealed class ScriptedProvider(
        Func<ModelRequest, int, IAsyncEnumerable<ModelDelta>> script) : IModelProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken) =>
            script(request, Interlocked.Increment(ref _calls));
    }

    private sealed class StaticContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                [ModelContextEntry.Text("turn-1", 1, ModelContextRole.User, "hi")]));
    }

    private sealed class EmptyPromptContextProvider : ISystemPromptContextProvider
    {
        public Task<SystemPromptContext> BuildAsync(
            AgentRun run,
            ModelContextSnapshot modelContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(SystemPromptContext.Empty);
    }

    private sealed class RecordingActivitySink : IAgentActivitySink
    {
        public List<AgentActivity> Activities { get; } = new();

        public void Publish(RunId runId, AgentActivity activity) => Activities.Add(activity);
    }

    private sealed class UnusedToolGateway : IToolGateway
    {
        public Task<ToolProposalDecision> EvaluateAsync(
            ToolProposal proposal,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no_tool_expected");

        public Task<PreparedToolExecution> PrepareAsync(
            ApprovedToolExecution execution,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no_tool_expected");

        public Task<ToolExecutionReceipt> ExecuteAsync(
            PreparedToolExecution execution,
            Func<ToolOutputChunk, ValueTask> publishDelta,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no_tool_expected");

        public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
