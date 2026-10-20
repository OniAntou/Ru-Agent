using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentRunProjectionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Projection_persists_history_budget_policy_and_version_state()
    {
        var run = Create(
            new HarnessRunStatus(
                HarnessRunState.WaitingHistory,
                HistoryWaitKind.UserBeforeFirstStep));

        Assert.Equal(7, run.Version);
        Assert.Equal(11, run.LastSequence);
        Assert.Equal(2, run.BudgetWindow);
        Assert.Equal(3, run.ModelStepsInWindow);
        Assert.Equal(5, run.ToolCallsInWindow);
        Assert.Equal(HistoryWaitKind.UserBeforeFirstStep, run.HistoryWaitKind);
        Assert.Equal(AgentPermissionPreset.Standard, run.Preset);
        Assert.Equal(4, run.PolicySnapshotVersion);
        Assert.Equal(new string('a', 64), run.PolicySnapshotSha256);
        Assert.Equal(CreatedAt, run.CreatedAt);
        Assert.Equal(CreatedAt.AddMinutes(1), run.UpdatedAt);
        Assert.Null(run.TerminalAt);
        Assert.Null(run.TerminalErrorCode);
        Assert.All(
            typeof(AgentRun).GetProperties(),
            property => Assert.False(property.CanWrite, property.Name));
    }

    [Fact]
    public void Stable_terminal_error_is_required_for_failure_and_cannot_change_via_setter()
    {
        var failedAt = CreatedAt.AddMinutes(2);
        var run = Create(
            new HarnessRunStatus(HarnessRunState.Failed),
            terminalAt: failedAt,
            terminalErrorCode: "model_step_failed");

        Assert.Equal(failedAt, run.TerminalAt);
        Assert.Equal("model_step_failed", run.TerminalErrorCode);
        Assert.Throws<ArgumentException>(() => Create(
            new HarnessRunStatus(HarnessRunState.Failed),
            terminalAt: failedAt));
        Assert.Throws<ArgumentException>(() => Create(
            new HarnessRunStatus(HarnessRunState.Running),
            terminalErrorCode: "model_step_failed"));
        Assert.Throws<ArgumentException>(() => Create(
            new HarnessRunStatus(HarnessRunState.Completed),
            terminalAt: failedAt,
            terminalErrorCode: "unexpected_error"));
    }

    [Fact]
    public void Projection_rejects_invalid_versions_budgets_hashes_and_timestamps()
    {
        Assert.Throws<InvalidOperationException>(() => Create(runId: default(RunId)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(preset: (AgentPermissionPreset)int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(version: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(lastSequence: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(budgetWindow: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(modelStepsInWindow: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(toolCallsInWindow: -1));
        Assert.Throws<ArgumentException>(() => Create(policySnapshotSha256: "not-a-hash"));
        Assert.Throws<ArgumentException>(() => Create(updatedAt: CreatedAt.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => Create(
            new HarnessRunStatus(HarnessRunState.Completed)));
        Assert.Throws<ArgumentException>(() => Create(
            new HarnessRunStatus(HarnessRunState.Running),
            terminalAt: CreatedAt.AddMinutes(2)));
    }

    private static AgentRun Create(
        HarnessRunStatus? status = null,
        RunId? runId = null,
        AgentPermissionPreset preset = AgentPermissionPreset.Standard,
        long version = 7,
        long lastSequence = 11,
        int budgetWindow = 2,
        int modelStepsInWindow = 3,
        int toolCallsInWindow = 5,
        string? policySnapshotSha256 = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? terminalAt = null,
        string? terminalErrorCode = null) =>
        new(
            runId ?? new RunId("run_11111111111111111111111111111111"),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            status ?? new HarnessRunStatus(HarnessRunState.Running),
            version,
            lastSequence,
            budgetWindow,
            modelStepsInWindow,
            toolCallsInWindow,
            preset,
            4,
            policySnapshotSha256 ?? new string('a', 64),
            CreatedAt,
            updatedAt ?? terminalAt ?? CreatedAt.AddMinutes(1),
            terminalAt,
            terminalErrorCode);
}
