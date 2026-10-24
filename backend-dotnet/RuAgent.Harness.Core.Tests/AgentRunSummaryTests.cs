using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed class AgentRunSummaryTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Completed_edit_without_verification_is_not_verified()
    {
        var summary = AgentRunSummary.FromTerminalRun(
            MakeRun(HarnessRunState.Completed),
            "Test Agent",
            new AgentRunSummaryMetrics(5, 1, 0, 0, true, false));

        Assert.Equal("succeeded", summary.Status);
        Assert.Equal("unverified", summary.VerificationState);
        Assert.Equal(5, summary.FilesScanned);
        Assert.Equal(1, summary.FilesChanged);
    }

    [Fact]
    public void Cancelled_run_without_evidence_is_not_reported_as_success()
    {
        var summary = AgentRunSummary.FromTerminalRun(
            MakeRun(HarnessRunState.Cancelled, "cancelled_by_user"),
            "Workspace");

        Assert.Equal("cancelled", summary.Status);
        Assert.Equal("not_applicable", summary.VerificationState);
    }

    [Fact]
    public void Summary_rejects_unsafe_label_and_out_of_range_counts()
    {
        Assert.Throws<ArgumentException>(() => new AgentRunSummary(
            "failed", "unverified", "D:\\secret", 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunSummary(
            "failed", "unverified", "Workspace", AgentRunSummary.MaximumCount + 1));
    }

    private static AgentRun MakeRun(HarnessRunState state, string? errorCode = null)
    {
        var terminal = state.IsTerminal() ? Timestamp.AddMinutes(1) : (DateTimeOffset?)null;
        return new AgentRun(
            new RunId("run_11111111111111111111111111111111"),
            new SessionId("11111111111111111111111111111111"),
            new WorkspaceId("ws_11111111111111111111111111111111"),
            new HarnessRunStatus(state),
            1,
            1,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Timestamp,
            terminal ?? Timestamp,
            terminal,
            errorCode);
    }
}
