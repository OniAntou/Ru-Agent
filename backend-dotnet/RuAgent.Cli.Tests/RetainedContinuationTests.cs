using RuAgent.Cli.Chat;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Attachments;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class RetainedContinuationTests
{
    [Fact]
    public async Task Known_continue_bypasses_a_paused_queue_without_claiming_pending_attachments()
    {
        using var pending = new ChatSubmissionState();
        pending.Add(new CliTextAttachment("draft.md", "unsent draft", 12));
        var view = new PlainChatView(TextWriter.Null, "workspace");
        var engine = new ChatSessionEngine(view);
        engine.SetRetainedContinuationClassifier(line => line.Equals("Continue", StringComparison.OrdinalIgnoreCase));
        engine.SetSubmissionSnapshotFactory(line => pending.CreateSnapshot(line, "s", "w", "standard", "b", "m", true));
        engine.SetSubmissionOwnershipHandlers(pending.Admit, _ => { });
        ChatSubmissionSnapshot? received = null;
        engine.SetSubmissionHandler((snapshot, _) => { received = snapshot; return Task.CompletedTask; });
        engine.PauseQueuedSubmissions();
        view.InputText = "Continue";
        engine.Submit();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(received);
        Assert.Empty(received.Images); Assert.Empty(received.Texts);
        Assert.Equal(1, pending.Count);
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public async Task Continue_without_a_known_run_does_not_bypass_a_held_queue()
    {
        var view = new PlainChatView(TextWriter.Null, "workspace");
        var engine = new ChatSessionEngine(view);
        engine.SetRetainedContinuationClassifier(_ => false);
        var dispatched = 0;
        engine.SetPromptHandler((_, _) => { dispatched++; return Task.CompletedTask; });
        engine.PauseQueuedSubmissions();
        view.InputText = "Continue"; engine.Submit();
        Assert.Equal(0, dispatched);
        engine.ResumeQueuedSubmissions();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, dispatched);
        engine.Stop(); await engine.Completion;
    }

    [Fact]
    public async Task Repeated_continue_during_recovery_stays_in_the_draft_and_is_not_queued()
    {
        using var errors = new StringWriter();
        var view = new PlainChatView(errors, "workspace");
        var engine = new ChatSessionEngine(view);
        engine.SetRetainedContinuationClassifier(line => line == "Continue");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = 0;
        engine.SetSubmissionSnapshotFactory(line =>
        {
            snapshots++;
            return ChatSubmissionSnapshot.Create(line, "s", "w", "standard", "b", "m", [], []);
        });
        engine.SetPromptHandler(async (_, ct) => await release.Task.WaitAsync(ct));
        view.InputText = "Continue"; engine.Submit();
        view.InputText = "Continue"; engine.Submit();
        Assert.Equal(1, snapshots);
        Assert.Equal("Continue", view.InputText);
        Assert.Contains("recovery is already active", errors.ToString());
        release.SetResult();
        await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        engine.Stop(); await engine.Completion;
    }

    [Theory]
    [InlineData("/follow")]
    [InlineData("/FOLLOW run_example")]
    public async Task Removed_follow_is_rejected_before_line_adapter_submission(string command)
    {
        using var errors = new StringWriter();
        var view = new PlainChatView(errors, "workspace"); var engine = new ChatSessionEngine(view);
        engine.SetSubmissionSnapshotFactory(_ => throw new InvalidOperationException("must not take draft ownership"));
        view.InputText = command; engine.Submit();
        Assert.Equal(command, view.InputText); Assert.Empty(engine.History);
        Assert.Contains("unknown_command", errors.ToString());
        engine.Stop(); await engine.Completion;
    }
}
