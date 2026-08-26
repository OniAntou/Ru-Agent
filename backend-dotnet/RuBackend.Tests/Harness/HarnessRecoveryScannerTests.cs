using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuBackend.Harness.Reconciliation;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessRecoveryScannerTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 24, 1, 2, 3, TimeSpan.Zero);

    private static readonly ExecutionId AmbiguousExecution =
        new("exec_11111111111111111111111111111111");

    private static readonly ToolCallId AmbiguousCall =
        new("call_11111111111111111111111111111111");

    [Fact]
    public async Task Crash_before_any_dispatch_fails_the_run_safely_without_effects()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);

            var summaries = await ScanAsync(fixture, tracked);

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("failed_pre_dispatch", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Incomplete_read_only_execution_fails_safely()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "read_only");

            var summaries = await ScanAsync(fixture, tracked);

            Assert.Equal("failed_read_only_incomplete", Assert.Single(summaries).Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Failed, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Incomplete_mutation_execution_requires_reconciliation()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "side_effects_possible");

            var summaries = await ScanAsync(fixture, tracked);

            var summary = Assert.Single(summaries);
            Assert.Equal(run.RunId, summary.RunId);
            Assert.Equal("reconciliation_required", summary.Action);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(after);
            Assert.Equal(HarnessRunState.ReconciliationRequired, after!.State);
            Assert.Contains(
                await ReadEventsAsync(fixture, run.RunId),
                item => item.Event.Kind == "reconciliation.required");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Fully_committed_executions_leave_a_running_run_untouched()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "read_only");
            await CommitCompletedAsync(fixture, run, AmbiguousExecution, AmbiguousCall);

            var summaries = await ScanAsync(fixture, tracked);

            Assert.Empty(summaries);
            var after = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None);
            Assert.Equal(HarnessRunState.Running, after!.State);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Waiting_approval_and_terminal_runs_are_never_touched()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
                        // The fixture seeds a fixed run id; only the first insert succeeds, so reseed
            // distinct runs through direct creation with unique identifiers.
            var tracked = new List<RunId>();
            var waiting = await CreateDistinctRunAsync(
                fixture, new HarnessRunStatus(HarnessRunState.WaitingApproval), "run_22222222222222222222222222222222");
            var completed = await CreateDistinctRunAsync(
                fixture, new HarnessRunStatus(HarnessRunState.Completed), "run_33333333333333333333333333333333");
            tracked.Add(waiting.RunId);
            tracked.Add(completed.RunId);

            var summaries = await ScanAsync(fixture, tracked);

            Assert.DoesNotContain(summaries, item => item.RunId == waiting.RunId);
            Assert.DoesNotContain(summaries, item => item.RunId == completed.RunId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recovery_never_reissues_an_old_mutation_execution_id()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var tracked = new List<RunId>();
            var run = await SeedRunningAsync(fixture, tracked);
            await CommitRequestedAsync(fixture, run, safeCode: "side_effects_possible");

            await ScanAsync(fixture, tracked);
            await ScanAsync(fixture, tracked);

            var events = await ReadEventsAsync(fixture, run.RunId);
            var requested = events.Where(item => item.Event.Kind == "tool.execution_requested").ToList();
            Assert.Single(requested);
            Assert.Equal(
                AmbiguousExecution.Value,
                Assert.Single(requested).Event.ExecutionId?.Value);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static async Task<AgentRun> SeedRunningAsync(BackendHarnessTestFixture fixture, List<RunId> tracked)
    {
        var run = await CreateDistinctRunAsync(
            fixture,
            new HarnessRunStatus(HarnessRunState.Running),
            $"run_{Guid.NewGuid():N}");
        tracked.Add(run.RunId);
        return run;
    }

    private static async Task<AgentRun> CreateDistinctRunAsync(
        BackendHarnessTestFixture fixture,
        HarnessRunStatus status,
        string rawRunId)
    {
        var workspace = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
            new WorkspaceId($"ws_{Guid.NewGuid():N}"),
            "volume-recovery",
            "root-file-recovery",
            new string('e', 64));
        await new RuAgent.Harness.Persistence.Workspace.SqliteWorkspaceStore(fixture.Options).RegisterAsync(
            new RuAgent.Harness.Persistence.Workspace.WorkspaceRegistration(
                workspace,
                new RuAgent.Harness.Persistence.Workspace.WorkspaceRootCiphertext(new byte[12], new byte[] { 1 }, new byte[16])),
            new RuAgent.Harness.Core.Workspace.WorkspaceTrustRequest(
                new TrustRequestId($"trust_{Guid.NewGuid():N}"),
                workspace,
                Timestamp,
                Timestamp.AddMinutes(10)),
            CancellationToken.None);
        var updatedAt = Timestamp;
        DateTimeOffset? terminalAt = null;
        if (status.State.IsTerminal())
        {
            updatedAt = Timestamp.AddMinutes(1);
            terminalAt = updatedAt;
        }

        var run = new AgentRun(
            new RunId(rawRunId),
            new SessionId(Guid.NewGuid().ToString("N")),
            workspace.WorkspaceId,
            status,
            0,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('f', 64),
            Timestamp,
            updatedAt,
            terminalAt);
        var request = new NewAgentRun(
            run,
            new[]
            {
                new HarnessEventIntent(
                    new EventId($"evt_{Guid.NewGuid():N}"),
                    HarnessEventKind.RunStateChanged),
            },
            Array.Empty<HarnessPayloadIntent>(),
            Array.Empty<ConversationOutboxIntent>(),
            new RunCreationIdentity(new string('7', 64), new string('8', 64), 1));
        var result = await fixture.EventStore.TryCreateRunAsync(request, CancellationToken.None);
        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
        return result.Run!;
    }

    private static async Task CommitEventAsync(
        BackendHarnessTestFixture fixture,
        AgentRun current,
        HarnessEventIntent intent)
    {
        var next = new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            current.Status,
            current.Version + 1,
            current.LastSequence + 1,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            Timestamp);
        var result = await fixture.EventStore.CommitAsync(
            new HarnessCommit(
                current.RunId,
                current.Version,
                next,
                new[] { intent },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>()),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
    }

    private static async Task CommitRequestedAsync(
        BackendHarnessTestFixture fixture,
        AgentRun run,
        string safeCode) =>
        await CommitEventAsync(
            fixture,
            run,
            new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionRequested,
                toolCallId: AmbiguousCall,
                executionId: AmbiguousExecution,
                safeCode: safeCode));

    private static async Task CommitCompletedAsync(
        BackendHarnessTestFixture fixture,
        AgentRun run,
        ExecutionId executionId,
        ToolCallId toolCallId)
    {
        var current = await fixture.EventStore.GetRunAsync(run.RunId, CancellationToken.None)
            ?? throw new InvalidOperationException("run missing");
        await CommitEventAsync(
            fixture,
            current,
            new HarnessEventIntent(
                NewEventId(),
                HarnessEventKind.ToolExecutionCompleted,
                toolCallId: toolCallId,
                executionId: executionId,
                safeCode: "ok",
                toolOutcome: ToolExecutionOutcome.Succeeded,
                sideEffectsPossible: false));
    }

    private static async Task<IReadOnlyList<HarnessEventEnvelope>> ReadEventsAsync(
        BackendHarnessTestFixture fixture,
        RunId runId)
    {
        var events = new List<HarnessEventEnvelope>();
        await foreach (var envelope in fixture.EventStore.ReadEventsAsync(runId, 0, CancellationToken.None))
        {
            events.Add(envelope);
        }

        return events;
    }

    private static async Task<IReadOnlyList<RecoveredRunSummary>> ScanAsync(
        BackendHarnessTestFixture fixture,
        IReadOnlyList<RunId> tracked) =>
        await new HarnessRecoveryScanner(
            fixture.EventStore,
            new FixedTime(Timestamp),
            _ => Task.FromResult<IReadOnlyList<RunId>>(tracked)).ScanPendingRunsAsync(CancellationToken.None);

    private static EventId NewEventId() =>
        new($"evt_{Guid.NewGuid():N}".Replace("-", "").PadRight(36)[..36]);

        private sealed class FixedTime(DateTimeOffset now) : ITimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
