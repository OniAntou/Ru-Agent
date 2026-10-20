using RuAgent.Cli.Attachments;
using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSubmissionStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedTransferReleasesTemporaryImagesOrRestoresThemBeforeDisposal(bool shouldRestore)
    {
        var path = Path.Combine(Path.GetTempPath(), "ru-transfer-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "image");
        using var state = new ChatSubmissionState();
        var image = new CliImageAttachment("image.png", "image/png", "aW1hZ2U=", 1, 1, 5, path);
        state.Add(image);
        using (var transfer = state.BeginTransfer(state.Images, state.Texts, true))
        {
            Assert.Equal(0, state.Count);
            Assert.True(File.Exists(path));
            if (shouldRestore)
            {
                transfer.ReturnToPending();
                transfer.ReturnToPending();
                Assert.Equal(1, state.Count);
            }
        }

        Assert.Equal(shouldRestore, File.Exists(path));
    }

    [Fact]
    public void AdmittedSnapshotKeepsIntentAndOwnsOnlyItsAttachments()
    {
        using var state = new ChatSubmissionState();
        var first = new CliTextAttachment("first.txt", "first", 5);
        var later = new CliTextAttachment("later.txt", "later", 5);
        state.Add(first);
        var snapshot = state.CreateSnapshot("fix", "session-a", "C:\\project", "standard", "OpenAI", "model-a", false)!;
        state.Add(later);

        state.Admit(snapshot);

        Assert.Equal("session-a", snapshot.SessionId);
        Assert.Equal("model-a", snapshot.Model);
        Assert.Equal(first, Assert.Single(snapshot.Texts));
        Assert.Equal(later, Assert.Single(state.Texts));
        Assert.Equal(1, state.Count);
    }

    [Fact]
    public void RetryRetainsFirstSnapshotAndItsIdempotencyKey()
    {
        using var state = new ChatSubmissionState();
        var first = state.CreateSnapshot("first", "session", "C:\\project", "standard", "OpenAI", "model", false)!;
        var second = state.CreateSnapshot("second", "session", "C:\\project", "standard", "OpenAI", "model", false)!;

        state.HoldForRetry(first);
        state.HoldForRetry(second);

        Assert.Same(first, state.Retryable);
        Assert.Equal(first.IdempotencyKey, state.Retryable!.IdempotencyKey);
        state.Accept();
        Assert.Null(state.Retryable);
    }

    [Fact]
    public void AdmissionDefersStateChangeCommandsAndRejectsOversizedComposedPrompt()
    {
        using var state = new ChatSubmissionState();
        state.Add(new CliTextAttachment("notes.txt", new string('x', ChatSubmissionState.MaxComposedPromptChars), 16_000));

        Assert.Null(state.CreateSnapshot("/new", "session", "C:\\project", "standard", "OpenAI", "model", false));
        Assert.Null(state.CreateSnapshot("fix", "session", "C:\\project", "standard", "OpenAI", "model", false));
        var control = state.CreateSnapshot("/model", "session", "C:\\project", "standard", "OpenAI", "model", false);
        Assert.NotNull(control);
        Assert.Empty(control.Texts);
        Assert.Equal(1, state.Count);
    }

    [Theory]
    [InlineData("/exit", false)]
    [InlineData("Continue", true)]
    public void ExitAndParkedContinue_LeavePendingAttachmentsOwnedByTheComposer(string line, bool parked)
    {
        using var state = new ChatSubmissionState();
        var temp = Path.Combine(Path.GetTempPath(), "ru-pending-control-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, "image");
        state.Add(new CliImageAttachment("image.png", "image/png", "aW1hZ2U=", 1, 1, 5, temp));
        state.Add(new CliTextAttachment("notes.txt", new string('x', ChatSubmissionState.MaxComposedPromptChars), 16_000));

        var snapshot = state.CreateSnapshot(line, "session", "C:\\project", "standard", "OpenAI", "model", parked);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Images);
        Assert.Empty(snapshot.Texts);
        state.Admit(snapshot);
        Assert.Equal(2, state.Count);
        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void TrustDeclineRestoresOwnedAttachmentsWithoutDeletingTemporaryImage()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ru-submission-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, "image");
        using var state = new ChatSubmissionState();
        var image = new CliImageAttachment("image.png", "image/png", "aW1hZ2U=", 1, 1, 5, temp);
        state.Add(image);
        var snapshot = state.CreateSnapshot("fix", "session", "C:\\project", "standard", "OpenAI", "model", false)!;

        state.Admit(snapshot);
        Assert.Equal(0, state.Count);
        Assert.True(File.Exists(temp));
        state.Restore(snapshot.Images, snapshot.Texts);
        Assert.Equal(image, Assert.Single(state.Images));
        Assert.True(File.Exists(temp));
        Assert.Equal("image.png", state.Detach(1));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void ClearAndDisposeReleasePendingImagesButSnapshotsRemainUsable()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ru-submission-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, "image");
        var state = new ChatSubmissionState();
        var image = new CliImageAttachment("image.png", "image/png", "aW1hZ2U=", 1, 1, 5, temp);
        state.Add(image);
        var snapshot = state.CreateSnapshot("fix", "session", "C:\\project", "standard", "OpenAI", "model", false)!;

        state.Dispose();
        state.Dispose();

        Assert.Equal(0, state.Count);
        Assert.False(File.Exists(temp));
        Assert.Equal("data:image/png;base64,aW1hZ2U=", Assert.Single(snapshot.Images).ToDataUrl());
        Assert.Null(state.Detach(0));
        Assert.Null(state.Detach(1));
    }
}
