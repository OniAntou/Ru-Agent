using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;
using ActivityKind = RuAgent.Protocol.Harness.AgentActivityKind;
using ActivityState = RuAgent.Protocol.Harness.AgentActivityState;

namespace RuBackend.Tests.Harness;

public sealed class AgentRunSummaryLivePublisherTests
{
    [Fact]
    public async Task Publisher_emits_safe_activity_and_one_typed_terminal_summary()
    {
        var hub = new HarnessLiveEventHub();
        var publisher = new AgentRunSummaryLivePublisher(hub);
        var run = MakeRun(HarnessRunState.Completed);
        publisher.RegisterWorkspace(run.RunId, "D:\\Test Agent");

        await using var lease = hub.Subscribe(run.RunId);
        publisher.PublishActivity(
            run.RunId,
            new AgentActivity("act_explore", ActivityKind.Explore, ActivityState.Completed, Count: 5));
        publisher.PublishActivity(
            run.RunId,
            new AgentActivity("act_edit", ActivityKind.Edit, ActivityState.Completed, FileName: "ChatView.xaml"));

        Assert.True(publisher.TryPublishSummary(run));
        Assert.False(publisher.TryPublishSummary(run));

        var frames = new List<TransientFrame>();
        while (lease.ActivityReader.TryRead(out var activityFrame))
        {
            frames.Add(activityFrame);
        }

        while (lease.Reader.TryRead(out var frame))
        {
            frames.Add(frame);
        }

        Assert.Equal(3, frames.Count);
        var summaryFrame = Assert.Single(frames, frame => frame.Kind == "agent.summary");
        var summary = AgentRunSummaryWireCodec.Parse(summaryFrame.Data);
        Assert.Equal("succeeded", summary.Status);
        Assert.Equal("verified", summary.VerificationState);
        Assert.Equal("Test Agent", summary.WorkspaceLabel);
        Assert.Equal(5, summary.FilesScanned);
        Assert.Equal(1, summary.FilesChanged);
        Assert.DoesNotContain("D:\\", summaryFrame.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Publisher_falls_back_to_bounded_workspace_label_and_does_not_build_nonterminal_summary()
    {
        var hub = new HarnessLiveEventHub();
        var publisher = new AgentRunSummaryLivePublisher(hub);
        var run = MakeRun(HarnessRunState.Running);
        publisher.RegisterWorkspace(run.RunId, "D:\\");

        Assert.False(publisher.TryReserveSummary(run, out _));
        var terminal = MakeRun(HarnessRunState.Cancelled, "cancelled_by_user");
        publisher.RegisterWorkspace(terminal.RunId, "D:\\");
        var summary = publisher.BuildSummary(terminal);
        Assert.Equal("cancelled", summary.Status);
        Assert.Null(summary.WorkspaceLabel);
    }

    [Fact]
    public void Failed_edit_does_not_claim_a_file_changed_but_marks_verification_unverified()
    {
        var hub = new HarnessLiveEventHub();
        var publisher = new AgentRunSummaryLivePublisher(hub);
        var run = MakeRun(HarnessRunState.Completed);

        publisher.PublishActivity(
            run.RunId,
            new AgentActivity("act_failed_edit", ActivityKind.Edit, ActivityState.Failed, FileName: "seed.txt"));

        var summary = publisher.BuildSummary(run);

        Assert.Null(summary.FilesChanged);
        Assert.Equal("unverified", summary.VerificationState);
    }

    [Fact]
    public void Terminal_summary_is_stable_when_a_late_activity_arrives()
    {
        var hub = new HarnessLiveEventHub();
        var publisher = new AgentRunSummaryLivePublisher(hub);
        var run = MakeRun(HarnessRunState.Completed);
        publisher.PublishActivity(
            run.RunId,
            new AgentActivity("act_edit", ActivityKind.Edit, ActivityState.Completed));

        Assert.True(publisher.TryPublishSummary(run));

        publisher.PublishActivity(
            run.RunId,
            new AgentActivity("act_late", ActivityKind.Edit, ActivityState.Completed));

        var summary = publisher.BuildSummary(run);
        Assert.Equal(1, summary.FilesChanged);
    }

    private static AgentRun MakeRun(HarnessRunState state, string? errorCode = null)
    {
        var created = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var terminal = state.IsTerminal() ? created.AddMinutes(1) : (DateTimeOffset?)null;
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
            created,
            terminal ?? created,
            terminal,
            errorCode);
    }
}
