using Microsoft.Extensions.Logging;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using RuBackend.Harness.ModelRunner;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// A drive pass can lose the run to another writer while a model step is in flight: a
/// client Stop, an expired approval, startup recovery, or a step that committed its own
/// bounded failure makes the run terminal first. The drive must publish that durable state
/// and release the run; it must not try to fail it a second time, log a settlement error,
/// or leave the registration behind.
/// </summary>
public sealed class AgentRuntimeSettlementRaceTests
{
    [Fact]
    public async Task A_run_cancelled_by_another_writer_during_a_step_is_released_without_a_failure()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunWithEncryptedWorkspaceAsync(
            new HarnessRunStatus(HarnessRunState.Running),
            "run_" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
        var roots = await RootsAsync(fixture, seeded);
        var recorded = new RecordingLogger<AgentRuntimeLoopService>();
        var provider = new ScriptedProvider(fixture, seeded.Run.RunId);
        var coordinator = Coordinator(fixture, provider, new RecordingActivitySink());
        using var loop = Loop(
            fixture,
            fixture.EventStore,
            roots,
            new SingleCoordinatorFactory(coordinator),
            recorded);

        await loop.StepOnceAsync(default);

        // The user's cancellation is the durable authority: no failure transition was
        // attempted, so the run keeps its cancelled state and has no failure fingerprint.
        var run = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(HarnessRunState.Cancelled, run!.State);
        Assert.Equal("cancelled", run.TerminalErrorCode);
        Assert.Null(run.TerminalFailure);

        var events = await ReadEventsAsync(fixture, seeded.Run.RunId);
        Assert.DoesNotContain(events, item => item.Kind == "run.failed");
        Assert.Contains(events, item => item.Kind == "run.state_changed" && item.SafeCode == "cancelled");

        // The run no longer holds a workspace root or a drive registration.
        Assert.False(roots.TryGetRoot(seeded.Run.RunId, out _));

        // The settlement is not reported as a failed failure-settlement: the old pass
        // logged "failed to mark run ... as failed" at error level and retried.
        Assert.DoesNotContain(recorded.Entries, entry => entry.Level == LogLevel.Error);

        // A following pass has nothing left to do and stays quiet.
        await loop.StepOnceAsync(default);
        Assert.DoesNotContain(recorded.Entries, entry => entry.Level == LogLevel.Error);
        Assert.False(roots.TryGetRoot(seeded.Run.RunId, out _));
    }

    private static AgentRunCoordinator Coordinator(
        BackendHarnessTestFixture fixture,
        ScriptedProvider provider,
        RecordingActivitySink activities)
    {
        var runner = new InProcessModelStepRunner(new ModelStepService(provider))
        {
            StepRetryBackoffsOverride = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            CreateProviderBootstrapAsync = (_, _) => Task.FromResult(Bootstrap()),
        };
        return new AgentRunCoordinator(
            runner,
            new UnusedToolGateway(),
            fixture.EventStore,
            new AgentContextAssembler(new StaticContextSource()),
            new CryptographicIdGenerator(),
            new SystemTimeProvider(),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            activitySink: activities,
            promptContextProvider: new EmptyPromptContextProvider());
    }

    private static async Task<List<HarnessEvent>> ReadEventsAsync(BackendHarnessTestFixture fixture, RunId runId)
    {
        var events = new List<HarnessEvent>();
        await foreach (var envelope in fixture.EventStore.ReadEventsAsync(runId, 0, CancellationToken.None))
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

    private static async Task<AgentRuntimeRootRegistry> RootsAsync(BackendHarnessTestFixture fixture, NewAgentRun seeded)
    {
        var workspace = Directory.GetDirectories(fixture.RootPath, "workspace-*").Single();
        var policy = await new HarnessPolicySnapshotService(fixture.Options, fixture.KeyStore, new SystemTimeProvider())
            .LoadAsync(
                await new WindowsWorkspaceIdentityResolver().ResolveAsync(workspace),
                seeded.Run.Preset,
                seeded.Run.RunId,
                CancellationToken.None);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(seeded.Run.RunId, workspace, policy);
        return roots;
    }

    private static AgentRuntimeLoopService Loop(
        BackendHarnessTestFixture fixture,
        IHarnessEventStore store,
        AgentRuntimeRootRegistry roots,
        IAgentRuntimeCoordinatorFactory factory,
        ILogger<AgentRuntimeLoopService> logger)
    {
        var sessions = new SessionStore(new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.log")));
        var dispatcher = new AgentConversationOutboxDispatcher(
            new HarnessOutboxStore(fixture.Options),
            fixture.PayloadStore,
            sessions,
            new HarnessModelContextBootstrapper(
                sessions,
                fixture.PayloadStore,
                new CryptographicIdGenerator(),
                TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots, store),
            TimeProvider.System);
        return new AgentRuntimeLoopService(
            dispatcher,
            store,
            factory,
            roots,
            logger,
            new WorkspaceCoordinatorStore(fixture.Options),
            new WorkspaceJournalProtector(fixture.KeyStore),
            new UnavailableToolGatewayFactory());
    }

    /// <summary>
    /// The scripted provider cancels the run the way a client Stop does while the model
    /// step is in flight, then fails the way the real runner does, so the step's failure
    /// settlement meets an already-terminal run.
    /// </summary>
    private sealed class ScriptedProvider(BackendHarnessTestFixture fixture, RunId runId) : IModelProvider
    {
        public async IAsyncEnumerable<ModelDelta> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            await CancelRunAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            throw new ModelProviderException("auth_failed", retryable: false);
        }

        private async Task CancelRunAsync(CancellationToken cancellationToken)
        {
            var run = await fixture.EventStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            Assert.NotNull(run);
            var cancelled = AgentV1Shared.AdvanceRun(
                run!,
                new HarnessRunStatus(HarnessRunState.Cancelled),
                run.BudgetWindow,
                run.ModelStepsInWindow,
                run.ToolCallsInWindow,
                1,
                DateTimeOffset.UtcNow,
                "cancelled");
            var commit = await fixture.EventStore.CommitAsync(
                new HarnessCommit(
                    run.RunId,
                    run.Version,
                    cancelled,
                    [new HarnessEventIntent(new CryptographicIdGenerator().NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "cancelled")],
                    [],
                    []),
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(HarnessCommitOutcome.Committed, commit.Outcome);
        }
    }

    private sealed class SingleCoordinatorFactory(AgentRunCoordinator coordinator) : IAgentRuntimeCoordinatorFactory
    {
        public Task<AgentRunCoordinator> CreateAsync(
            string root,
            PolicySnapshot? policySnapshot = null,
            RunId? runId = null,
            string? agentSessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(coordinator);

        public void DropRetainedApproval(RunId runId)
        {
        }
    }

    private sealed class StaticContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, CancellationToken cancellationToken) =>
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
        public Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no_tool_expected");

        public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no_tool_expected");

        public Task<ToolExecutionReceipt> ExecuteAsync(
            PreparedToolExecution execution,
            Func<ToolOutputChunk, ValueTask> publishDelta,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no_tool_expected");

        public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
