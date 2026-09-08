using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ApprovalWorkflowTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    private static ToolApprovalBinding Binding(
        string? path = @"C:\ws\target.txt",
        string? executable = null,
        IReadOnlyList<string>? argv = null,
        string? cwd = null,
        string operationClass = "read",
        string networkIntent = "none",
        long snapshotVersion = 3,
        string? intentHash = null,
        DateTimeOffset? expiresAt = null) => new(
        new RunId("run_" + new string('a', 32)),
        new ToolCallId("call_" + new string('b', 32)),
        "files.write",
        "1.0.0",
        path,
        executable,
        argv ?? Array.Empty<string>(),
        cwd,
        operationClass,
        networkIntent,
        snapshotVersion,
        intentHash ?? new string('0', 64),
        expiresAt ?? Epoch.AddMinutes(30));

    [Fact]
    public void Allow_once_consumes_exactly_one_time()
    {
        var ledger = new PendingApprovalLedger();
        ledger.Issue("apr_" + new string('c', 32), Binding());

        var first = ledger.Resolve(
            "apr_" + new string('c', 32),
            ApprovalCommand.AllowOnce,
            Binding(),
            Epoch);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, first.Outcome);

        var replay = ledger.Resolve(
            "apr_" + new string('c', 32),
            ApprovalCommand.AllowOnce,
            Binding(),
            Epoch);
        Assert.Equal(ApprovalOutcome.NotFound, replay.Outcome);
    }

    [Fact]
    public void Deny_consumes_as_denied()
    {
        var ledger = new PendingApprovalLedger();
        ledger.Issue("apr_" + new string('d', 32), Binding());

        var resolution = ledger.Resolve(
            "apr_" + new string('d', 32),
            ApprovalCommand.Deny,
            Binding(),
            Epoch);

        Assert.Equal(ApprovalOutcome.ConsumedDenied, resolution.Outcome);
    }

    [Fact]
    public void Allow_once_carries_no_reusable_rule()
    {
        var pending = PendingToolApproval.Issue("apr_" + new string('f', 32), Binding());

        var resolution = ApprovalState.TryResolve(pending, ApprovalCommand.AllowOnce, Binding(), Epoch);

        Assert.Equal(ApprovalOutcome.ConsumedApproved, resolution.Outcome);
    }

    [Fact]
    public void Expired_approval_requires_new_request_instead_of_executing()
    {
        var binding = Binding(expiresAt: Epoch.AddMinutes(-1));
        var pending = PendingToolApproval.Issue("apr_" + new string('1', 32), binding);

        var expired = ApprovalState.TryResolve(pending, ApprovalCommand.AllowOnce, binding, Epoch);

        Assert.Equal(ApprovalOutcome.Expired, expired.Outcome);

        // Re-evaluation issues a fresh approval which then resolves normally.
        var fresh = PendingToolApproval.Issue("apr_" + new string('2', 32), Binding());
        var retry = ApprovalState.TryResolve(fresh, ApprovalCommand.AllowOnce, Binding(), Epoch);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, retry.Outcome);
    }

    [Theory]
    [InlineData("run_id")]
    [InlineData("tool_call_id")]
    [InlineData("tool_id")]
    [InlineData("tool_version")]
    [InlineData("canonical_path")]
    [InlineData("resolved_executable")]
    [InlineData("argv_pattern")]
    [InlineData("cwd")]
    [InlineData("operation_class")]
    [InlineData("network_intent")]
    [InlineData("snapshot_version")]
    [InlineData("intent_hash")]
    public void Any_changed_binding_field_returns_stale(string changedField)
    {
        var binding = Binding(
            argv: new[] { "x" },
            executable: @"C:\tools\tool.exe",
            cwd: @"C:\ws");
        var pending = PendingToolApproval.Issue("apr_" + new string('9', 32), binding);

        ToolApprovalBinding mutated = changedField switch
        {
            "run_id" => binding with
            {
                RunId = new RunId("run_" + new string('7', 32)),
            },
            "tool_call_id" => binding with
            {
                ToolCallId = new ToolCallId("call_" + new string('8', 32)),
            },
            "tool_id" => binding with { ToolId = "files.edit" },
            "tool_version" => binding with { ToolVersion = "1.0.1" },
            "canonical_path" => binding with { CanonicalPath = @"C:\ws\other.txt" },
            "resolved_executable" => binding with { ResolvedExecutable = @"C:\tools\other.exe" },
            "argv_pattern" => binding with { Argv = new[] { "x", "y" } },
            "cwd" => binding with { Cwd = @"C:\ws\sub" },
            "operation_class" => binding with { OperationClass = "edit" },
            "network_intent" => binding with { NetworkIntent = "declared" },
            "snapshot_version" => binding with { SnapshotVersion = binding.SnapshotVersion + 1 },
            _ => binding with { IntentHash = new string('1', 64) },
        };

        var resolution = ApprovalState.TryResolve(pending, ApprovalCommand.AllowOnce, mutated, Epoch);

        Assert.Equal(ApprovalOutcome.Stale, resolution.Outcome);
    }

    [Fact]
    public void Null_pending_resolves_to_not_found()
    {
        var resolution = ApprovalState.TryResolve(null, ApprovalCommand.AllowOnce, Binding(), Epoch);

        Assert.Equal(ApprovalOutcome.NotFound, resolution.Outcome);
    }

    [Fact]
    public async Task Approval_wait_and_resume_do_not_consume_model_step_or_tool_call_budget()
    {
        var store = new InMemoryHarnessEventStore();
        var run = Run(HarnessRunState.Running, modelStepsInWindow: 2, toolCallsInWindow: 3);
        store.Seed(run);
        var coordinator = Coordinator(store);

        var waiting = await coordinator.EnterApprovalWaitAsync(
            run.RunId,
            new ApprovalId("apr_" + new string('4', 32)),
            CancellationToken.None);

        Assert.Equal(HarnessRunState.WaitingApproval, waiting.State);
        Assert.Equal(2, waiting.ModelStepsInWindow);
        Assert.Equal(3, waiting.ToolCallsInWindow);

        var resumed = await coordinator.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);

        Assert.Equal(HarnessRunState.Running, resumed.State);
        Assert.Equal(2, resumed.ModelStepsInWindow);
        Assert.Equal(3, resumed.ToolCallsInWindow);
        Assert.Contains(
            store.Commits,
            commit => commit.Events.Any(@event => @event.Kind == HarnessEventKind.ApprovalRequested));
    }

    [Fact]
    public async Task Stale_or_terminal_resolution_has_no_effect_on_other_runs()
    {
        var store = new InMemoryHarnessEventStore();
        var run = Run(HarnessRunState.Running);
        store.Seed(run);
        var coordinator = Coordinator(store);
        await coordinator.EnterApprovalWaitAsync(
            run.RunId,
            new ApprovalId("apr_" + new string('5', 32)),
            CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            coordinator.ResumeAfterApprovalAsync(
                new RunId("run_" + new string('6', 32)),
                CancellationToken.None));

        var stillResumable = await coordinator.ResumeAfterApprovalAsync(run.RunId, CancellationToken.None);
        Assert.Equal(HarnessRunState.Running, stillResumable.State);
    }

    [Fact]
    public async Task Approval_wait_is_illegal_outside_running_state()
    {
        var store = new InMemoryHarnessEventStore();
        var completedRun = Run(HarnessRunState.Completed);
        store.Seed(completedRun);
        var coordinator = Coordinator(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.EnterApprovalWaitAsync(
                completedRun.RunId,
                new ApprovalId("apr_" + new string('7', 32)),
                CancellationToken.None));
    }

    private static AgentRunCoordinator Coordinator(InMemoryHarnessEventStore store) =>
        new(
            new FakeModelStepRunner(),
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(new EmptyContextSource()),
            new SequenceIdGenerator(),
            new FixedTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            promptContextProvider: new FakeSystemPromptContextProvider());

    private static AgentRun Run(
        HarnessRunState state,
        int modelStepsInWindow = 0,
        int toolCallsInWindow = 0)
    {
        var status = new HarnessRunStatus(state);
        var isTerminal = state is HarnessRunState.Completed
            or HarnessRunState.Failed
            or HarnessRunState.Cancelled
            or HarnessRunState.Interrupted;
        return new(
            new RunId("run_" + new string('0', 32)),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            status,
            version: 0,
            lastSequence: 0,
            budgetWindow: 1,
            modelStepsInWindow,
            toolCallsInWindow,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            new string('a', 64),
            Epoch,
            Epoch,
            terminalAt: isTerminal ? Epoch : null,
            terminalErrorCode: state is HarnessRunState.Failed or HarnessRunState.Interrupted ? "test_terminal" : null);
    }

    private sealed class EmptyContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                Array.Empty<ModelContextEntry>()));
    }

    private sealed class FixedTimeProvider : ITimeProvider
    {
        public FixedTimeProvider(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private ulong _counter;

        public RunId NewRunId() => new($"run_{++_counter:x32}");
        public ModelStepId NewModelStepId() => new($"step_{++_counter:x32}");
        public ToolCallId NewToolCallId() => new($"call_{++_counter:x32}");
        public ExecutionId NewExecutionId() => new($"exec_{++_counter:x32}");
        public ApprovalId NewApprovalId() => new($"apr_{++_counter:x32}");
        public TrustRequestId NewTrustRequestId() => new($"trust_{++_counter:x32}");
        public PolicyRuleId NewPolicyRuleId() => new($"rule_{++_counter:x32}");
        public EventId NewEventId() => new($"evt_{++_counter:x32}");
        public PayloadId NewPayloadId() => new($"payload_{++_counter:x32}");
    }
}
