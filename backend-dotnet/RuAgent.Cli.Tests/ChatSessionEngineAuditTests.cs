using RuAgent.Cli.Chat;
using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSessionEngineAuditTests
{
    [Fact]
    public async Task Stop_between_snapshot_and_admission_never_transfers_attachment_ownership()
    {
        using var pending = new ChatSubmissionState();
        pending.Add(new CliImageAttachment("clipboard.png", "image/png", "AA==", 1, 1, 1));
        var view = new PlainChatView(TextWriter.Null, "workspace") { InputText = "late draft" };
        var engine = new ChatSessionEngine(view);
        var admitted = 0;
        engine.SetTaskCommandHandler((_, _) => Task.CompletedTask); // Match production inspection wiring.
        engine.SetSubmissionSnapshotFactory(line =>
        {
            var snapshot = pending.CreateSnapshot(line, "session", "workspace", "standard", "OpenAI", "model-a", false);
            engine.Stop(); // Deterministic shutdown between validation and queue admission.
            return snapshot;
        });
        engine.SetSubmissionOwnershipHandlers(snapshot => { admitted++; pending.Admit(snapshot); }, _ => { });
        engine.Submit();
        await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admitted);
        Assert.Equal("late draft", view.InputText);
        Assert.Equal(1, pending.Count);
        Assert.Empty(engine.History);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopped_controller_keeps_late_drafts_and_attachments_with_their_owner(bool turnStillSettling)
    {
        var temporaryImage = Path.Combine(Path.GetTempPath(), "ru-audit-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllTextAsync(temporaryImage, "synthetic image ownership fixture");
        using var pending = new ChatSubmissionState();
        var view = new PlainChatView(TextWriter.Null, "workspace");
        var engine = new ChatSessionEngine(view);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new List<string>();
        var factories = 0;
        engine.SetSubmissionSnapshotFactory(line =>
        {
            factories++;
            return pending.CreateSnapshot(line, "session", "workspace", "standard", "OpenAI", "model-a", false);
        });
        engine.SetSubmissionOwnershipHandlers(snapshot => { admitted.Add(snapshot.Prompt); pending.Admit(snapshot); },
            snapshot => ChatSubmissionState.Release(snapshot.Images));
        engine.SetPromptHandler((_, _) => turnStillSettling ? release.Task : Task.CompletedTask);
        try
        {
            view.InputText = "first turn";
            engine.Submit();
            pending.Add(new CliImageAttachment("clipboard.png", "image/png", "AA==", 1, 1, 1, temporaryImage));
            engine.Stop();
            view.InputText = "late draft";
            engine.Submit();

            Assert.Equal(["first turn"], admitted);
            Assert.Equal(1, factories);
            Assert.Equal("late draft", view.InputText);
            Assert.Equal(1, pending.Count);
            Assert.True(File.Exists(temporaryImage));
            Assert.Equal(["first turn"], engine.History);
        }
        finally
        {
            release.TrySetResult();
            await engine.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            engine.Stop();
            await engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(temporaryImage);
        }
    }
}
