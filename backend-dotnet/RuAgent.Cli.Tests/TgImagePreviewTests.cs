using RuAgent.Cli.Attachments;
using RuAgent.Cli.Images;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgImagePreviewTests
{
    private static readonly byte[] ImageBytes = CreatePng();
    private static readonly string ImageDataUrl = "data:image/png;base64," + Convert.ToBase64String(ImageBytes);
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Paste_shows_images_in_the_composer_without_changing_the_draft(bool home, bool busy)
    {
        using var state = new ChatSubmissionState();
        Render(home, 80,
            (app, window, loop) =>
            {
                Wire(state, loop);
                if (busy)
                {
                    loop.SetPromptHandler(async (_, _, ct) => await Task.Delay(System.Threading.Timeout.Infinite, ct));
                    window.Composer.Input.Text = "running turn";
                    loop.Submit();
                }
                window.Composer.Input.Text = "describe this";
                state.Add(new CliTextAttachment("notes.txt", "notes", 5));
                loop.SetPasteHandler(() =>
                {
                    state.Add(Image("first.png"));
                    state.Add(Image("second.png"));
                    loop.RefreshAttachmentLabel();
                    return Task.CompletedTask;
                });
                app.InjectKey(Key.V.WithCtrl);
            },
            (app, window, _) =>
            {
                Assert.Contains("[image 1] [image 2]", PreviewRow(app, window));
                Assert.Equal("describe this", window.Composer.Input.Text);
                Assert.Equal(TgComposerView.RowCount, window.Composer.LayoutHeight);
                Assert.Equal(busy, window.Hints.IsRunning);
                Assert.Equal(home, window.Landing.Visible);
                Assert.DoesNotContain("[image 3]", PreviewRow(app, window));
            });
    }

    [Theory]
    [InlineData(34)]
    [InlineData(80)]
    public void Images_follow_a_multiline_draft_without_overwriting_metadata(int width)
    {
        Render(false, width,
            (_, window, _) =>
            {
                window.Composer.Input.Text = "first line\nsecond line\nthird line";
                window.Composer.SetMeta("standard", "vision-model", "provider");
                window.Composer.SetImages(4);
            },
            (app, window, _) =>
            {
                var screen = TgSnapshot.Capture(app);
                Assert.Contains("[image 1]", PreviewRow(app, window));
                Assert.Contains("third line", screen[window.Composer.Frame.Y + window.Composer.InputHeight]);
                Assert.Contains("standard", screen[window.Composer.Frame.Y + window.Composer.InputHeight + 2]);
                Assert.Equal(3, window.Composer.InputHeight);
                Assert.Equal(7, window.Composer.LayoutHeight);
                if (width == 80) Assert.Contains("[image 4]", PreviewRow(app, window));
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("look at this")]
    public void Sending_clears_markers_and_keeps_real_images_outside_the_prompt(string prompt)
    {
        using var state = new ChatSubmissionState();
        ChatSubmissionSnapshot? sent = null;
        Render(false, 80,
            (_, window, loop) =>
            {
                Wire(state, loop);
                loop.SetSubmissionSnapshotFactory(line => state.CreateSnapshot(
                    line, "session", "C:\\project", "standard", "provider", "vision-model", false));
                loop.SetSubmissionOwnershipHandlers(snapshot =>
                {
                    state.Admit(snapshot);
                    loop.RefreshAttachmentLabel();
                }, _ => { });
                loop.SetSubmissionHandler((snapshot, _, _) =>
                {
                    sent = snapshot;
                    return Task.CompletedTask;
                });
                state.Add(Image("first.png"));
                state.Add(Image("second.png"));
                loop.RefreshAttachmentLabel();
                window.Composer.Input.Text = prompt;
            },
            (app, window, loop) =>
            {
                Assert.Contains("[image 1] [image 2]", PreviewRow(app, window));
                loop.Submit();
            },
            (app, window, _) =>
            {
                Assert.DoesNotContain("[image", PreviewRow(app, window));
                Assert.NotNull(sent);
                Assert.Equal(prompt, sent.Prompt);
                Assert.Equal(2, sent.Images.Length);
                Assert.All(sent.Images, image => Assert.Equal(ImageDataUrl, image.ToDataUrl()));
                Assert.Empty(state.Images);
                Assert.Equal(string.Empty, window.Composer.Input.Text);
            });
    }

    [Fact]
    public void Queued_images_leave_the_draft_and_do_not_consume_later_pastes()
    {
        using var state = new ChatSubmissionState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ChatSubmissionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Render(false, 80,
            (_, window, loop) =>
            {
                Wire(state, loop);
                loop.SetSubmissionSnapshotFactory(line => state.CreateSnapshot(
                    line, "session", "C:\\project", "standard", "provider", "vision-model", false));
                loop.SetSubmissionOwnershipHandlers(snapshot =>
                {
                    state.Admit(snapshot);
                    loop.RefreshAttachmentLabel();
                }, _ => { });
                loop.SetSubmissionHandler(async (snapshot, _, ct) =>
                {
                    if (snapshot.Prompt == "running") await release.Task.WaitAsync(ct);
                    else received.TrySetResult(snapshot);
                });
                window.Composer.Input.Text = "running";
                loop.Submit();
                state.Add(Image("queued.png"));
                loop.RefreshAttachmentLabel();
            },
            (app, window, loop) =>
            {
                Assert.Contains("[image 1]", PreviewRow(app, window));
                window.Composer.Input.Text = "queued image";
                loop.Submit();
            },
            (app, window, loop) =>
            {
                Assert.DoesNotContain("[image", PreviewRow(app, window));
                Assert.Equal(1, window.Hints.QueuedCount);
                state.Add(Image("later.png"));
                loop.RefreshAttachmentLabel();
                release.TrySetResult();
            },
            (app, window, _) =>
            {
                Assert.True(received.Task.IsCompletedSuccessfully);
                Assert.Equal("queued.png", Assert.Single(received.Task.Result.Images).FileName);
                Assert.Equal(ImageDataUrl, Assert.Single(received.Task.Result.Images).ToDataUrl());
                Assert.Equal("later.png", Assert.Single(state.Images).FileName);
                Assert.Contains("[image 1]", PreviewRow(app, window));
            });
    }

    [Fact]
    public void Failed_send_stays_visible_for_retry_and_acceptance_removes_only_retry_markers()
    {
        using var state = new ChatSubmissionState();
        Render(false, 80,
            (_, _, loop) =>
            {
                Wire(state, loop);
                state.Add(Image("failed.png"));
                var snapshot = state.CreateSnapshot("describe", "session", "C:\\project", "standard", "provider", "model", false)!;
                state.Admit(snapshot);
                state.HoldForRetry(snapshot);
                loop.RefreshAttachmentLabel();
            },
            (app, _, loop) =>
            {
                Assert.Contains("/retry: [image 1]", string.Join('\n', TgSnapshot.Capture(app)));
                Assert.Equal(ImageDataUrl, Assert.Single(state.Retryable!.Images).ToDataUrl());
                state.Add(Image("new.png"));
                state.Accept();
                loop.RefreshAttachmentLabel();
            },
            (app, window, _) =>
            {
                Assert.Contains("[image 1]", PreviewRow(app, window));
                Assert.DoesNotContain("/retry", PreviewRow(app, window));
                Assert.Equal("new.png", Assert.Single(state.Images).FileName);
            });
    }

    [Fact]
    public void Trust_decline_restores_markers_and_clearing_removes_stale_cells()
    {
        using var state = new ChatSubmissionState();
        Render(false, 80,
            (_, _, loop) =>
            {
                Wire(state, loop);
                state.Add(Image("restored.png"));
                var snapshot = state.CreateSnapshot("", "session", "C:\\project", "standard", "provider", "model", false)!;
                state.Admit(snapshot);
                using var transfer = state.BeginTransfer(snapshot.Images, snapshot.Texts, false);
                transfer.ReturnToPending();
                loop.RefreshAttachmentLabel();
            },
            (app, window, loop) =>
            {
                Assert.Contains("[image 1]", PreviewRow(app, window));
                state.ClearPending();
                loop.RefreshAttachmentLabel();
            },
            (app, window, _) => Assert.DoesNotContain("[image", PreviewRow(app, window)));
    }

    private static CliImageAttachment Image(string name) =>
        new(name, "image/png", Convert.ToBase64String(ImageBytes), 1, 1, ImageBytes.Length);

    private static byte[] CreatePng()
    {
        using var bitmap = new System.Drawing.Bitmap(1, 1);
        bitmap.SetPixel(0, 0, System.Drawing.Color.CornflowerBlue);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static void Wire(ChatSubmissionState state, TgChatLoop loop)
    {
        loop.SetAttachmentCountProvider(() => state.Count);
        loop.SetImageCountProvider(() => state.Images.Count, () => state.Retryable?.Images.Length ?? 0);
    }

    private static string PreviewRow(IApplication app, TgChatWindow window) =>
        TgSnapshot.Capture(app)[window.Composer.Frame.Y + window.Composer.InputHeight + 1];

    private static void Render(bool home, int width, params Action<IApplication, TgChatWindow, TgChatLoop>[] frames)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        if (!home) window.Transcript.Append("Existing conversation", TranscriptStyle.Prose);
        var index = -1;
        Exception? failure = null;
        var completed = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (index < 0)
                {
                    app.Driver!.SetScreenSize(width, 24);
                    window.FocusComposer();
                    index = 0;
                    return true;
                }
                if (app.Driver!.Cols != width || app.Driver.Rows != 24)
                {
                    app.Driver.SetScreenSize(width, 24);
                    return true;
                }
                frames[index++](app, window, loop);
                if (index < frames.Length) return true;
                completed = true;
            }
            catch (Exception error)
            {
                failure = error;
            }
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () =>
        {
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(completed, $"Preview test did not finish: frame {index}, screen {app.Driver?.Cols}x{app.Driver?.Rows}.");
    }
}
