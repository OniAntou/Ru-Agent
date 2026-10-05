using RuAgent.Cli.Attachments;
using RuAgent.Cli;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class WorkStateHandoffContentTests
{
    [Fact]
    public async Task Workflow_requires_preview_then_target_bound_attachment_and_keeps_selection_on_cap_rejection()
    {
        var workflow = new WorkStateHandoffWorkflow();
        var preview = await workflow.PreviewAsync(
            "run_selected", "session_target", (_, _) => Task.FromResult(Run()), () => true, CancellationToken.None);
        var attachments = new List<CliTextAttachment>();

        Assert.True(preview.IsReady);
        Assert.False(workflow.TryAttach("session_other", attachments, 0, 0, 8));
        Assert.False(workflow.TryAttach("session_target", attachments, 8, 0, 8));
        Assert.False(workflow.TryAttach("session_target", attachments, 1, ChatProjectFileWorkflow.MaxPendingAttachmentBytes - 1, 8));
        Assert.Empty(attachments);
        Assert.True(workflow.TryAttach("session_target", attachments, 0, 0, 8));
        Assert.Equal(preview.Preview, Assert.Single(attachments).TextContent);
    }

    [Fact]
    public async Task Workflow_discards_delayed_preview_when_target_changes_and_bounds_errors()
    {
        var workflow = new WorkStateHandoffWorkflow();
        var completion = new TaskCompletionSource<GetAgentRunResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var isTargetCurrent = true;
        var pending = workflow.PreviewAsync("run_selected", "session_target", (_, _) => completion.Task,
            () => isTargetCurrent, CancellationToken.None);
        isTargetCurrent = false;
        completion.SetResult(Run());

        var result = await pending;
        Assert.False(result.IsReady);
        Assert.Contains("target_changed", result.Error);
        Assert.False(workflow.TryAttach("session_target", new List<CliTextAttachment>(), 0, 0, 8));
    }

    [Fact]
    public void Preview_preserves_source_and_marks_unknown_workspace_and_partial_state()
    {
        var content = WorkStateHandoffContent.Create(Run());

        Assert.NotNull(content);
        Assert.Contains("Selected run: run_selected", content);
        Assert.Contains("Session: session_source", content);
        Assert.Contains("Latest state source run: run_state_source", content);
        Assert.Contains("Last updated: 2026-10-04T12:00:00Z", content);
        Assert.Contains("target workspace identity is unknown", content);
        Assert.Contains("partial", content);
        Assert.Contains("untrusted", content);
        Assert.Contains("saved unfinished task", content);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(content) <= WorkStateHandoffFormatter.MaxAttachmentUtf8Bytes);
    }

    [Fact]
    public void Preview_refuses_unavailable_or_oversized_state()
    {
        Assert.Null(WorkStateHandoffContent.Create(Run() with { WorkStateStatus = "unavailable" }));
        Assert.Null(WorkStateHandoffContent.Create(Run() with { WorkStatePreview = new string('界', 5_000) }));
    }

    private static GetAgentRunResponse Run() => new(
        "run_selected", "session_source", "workspace_source", "succeeded", null, "standard",
        1, "sha", 2, 40, "2026-10-04", "2026-10-04", "2026-10-04", null,
        WorkStateStatus: "available", WorkStatePartial: true, WorkStateIntentOmitted: true,
        WorkStatePreviewOmitted: true, WorkStateSourceRunId: "run_state_source",
        WorkStateSourceEventId: "event_18", WorkStateUpdatedAt: "2026-10-04T12:00:00Z",
        WorkStatePreview: "saved unfinished task; stale file evidence");
}
