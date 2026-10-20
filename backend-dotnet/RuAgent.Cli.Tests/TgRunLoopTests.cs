using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgRunLoopTests
{
    [Fact]
    public async Task Submit_EchoesUserBlock_StreamsProse_AndAppendsToolRows()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);

        loop.SetPromptHandler(async (line, stream, ct) =>
        {
            stream.Append(" Em kiểm tra lock", TranscriptStyle.Prose);
            stream.AppendChunk(" trong harness.");
            stream.Append(" ✱ Search \"busy lock\" (18 matches)", TranscriptStyle.Tool);
            await Task.CompletedTask;
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "sửa lỗi";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("sửa lỗi", text);
            Assert.Contains("Em kiểm tra lock trong harness.", text);
            Assert.Contains("Search", text);
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    /// <summary>
    /// Task 8's parked finding: focus granted to the composer after the run starts must
    /// win against a view that already owns focus. The real wiring calls
    /// <see cref="TgChatWindow.FocusComposer"/> from a timeout once the window is live.
    /// </summary>
    [Fact]
    public void FocusComposer_TakesFocusFromACompetingView()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var competitor = new View { X = 0, Y = 0, Width = 1, Height = 1, CanFocus = true };
        window.Add(competitor);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            competitor.SetFocus();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.True(competitor.HasFocus);
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            Assert.True(window.Composer.Input.HasFocus);
            Assert.True(window.Composer.Input.Cursor?.IsVisible);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void HistoryUpDown_ReplacesTheBuffer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);
        loop.RememberForTests("lần trước");

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.CursorUp);
            Assert.Equal("lần trước", window.Composer.Input.Text?.ToString());
            app.InjectKey(Key.CursorDown);
            Assert.Equal(string.Empty, window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public async Task SubmitWhileBusy_QueuesInsteadOfInterleaving()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        loop.SetPromptHandler(async (line, _, _) => { seen.Add(line); await gate.Task; });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "một";
            loop.Submit();
            window.Composer.Input.Text = "hai";
            loop.Submit();
            Assert.Equal(["một"], seen);
            gate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            Assert.Equal(["một", "hai"], seen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    [Fact]
    public async Task QueuedPrompts_KeepTheAttachmentsPresentAtAdmission()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var gate = new TaskCompletionSource();
        var pendingAttachments = new List<string>();
        var observed = new List<string>();
        loop.SetAttachmentCountProvider(() => pendingAttachments.Count);
        loop.SetSubmissionSnapshotFactory(line => ChatSubmissionSnapshot.Create(
            line,
            "sess_current",
            "C:\\workspace",
            "standard",
            "backend",
            "model",
            [],
            pendingAttachments.Select(name => new RuAgent.Cli.Attachments.CliTextAttachment(name, name, name.Length))));
        loop.SetSubmissionHandler(async (submission, _, _) =>
        {
            if (submission.Prompt == "active")
            {
                await gate.Task;
                return;
            }

            observed.Add($"{submission.Prompt}:{string.Join(',', submission.Texts.Select(file => file.FileName))}");
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "active";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            pendingAttachments.Add("attachment-A");
            window.Composer.Input.Text = "message-1";
            loop.Submit();
            pendingAttachments.Clear();
            pendingAttachments.Add("attachment-B");
            window.Composer.Input.Text = "message-2";
            loop.Submit();
            gate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(800), () =>
        {
            Assert.Equal(["message-1:attachment-A", "message-2:attachment-B"], observed);
            app.RequestStop();
            return false;
        });

        app.Run(window);
        await loop.Completion;
    }

    [Fact]
    public async Task RetryPause_RepairCommandRunsImmediatelyAndPreservesPendingAttachments()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var activeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        var pendingAttachments = new List<RuAgent.Cli.Attachments.CliTextAttachment>();
        loop.SetAttachmentCountProvider(() => pendingAttachments.Count);
        loop.SetSubmissionSnapshotFactory(line => ChatSubmissionSnapshot.Create(
            line,
            "sess_current",
            "C:\\workspace",
            "standard",
            "backend",
            "model",
            [],
            ChatSubmissionSnapshot.IsPausedControlCommand(line)
                ? Array.Empty<RuAgent.Cli.Attachments.CliTextAttachment>()
                : pendingAttachments.ToArray()));
        loop.SetSubmissionOwnershipHandlers(
            submission =>
            {
                foreach (var attachment in submission.Texts)
                {
                    pendingAttachments.Remove(attachment);
                }
            },
            _ => { });
        loop.SetStateChangingCommandClassifier(command => command.StartsWith("/", StringComparison.Ordinal));
        loop.SetSubmissionHandler(async (submission, _, _) =>
        {
            seen.Add(submission.Prompt);
            if (submission.Prompt == "active")
            {
                await activeGate.Task;
            }
            else if (submission.Prompt is "message-1" or "message-2")
            {
                loop.PauseQueuedSubmissions();
            }
            else if (submission.Prompt == "/retry")
            {
                loop.ResumeQueuedSubmissions();
            }
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "active";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            window.Composer.Input.Text = "message-1";
            loop.Submit();
            window.Composer.Input.Text = "message-2";
            loop.Submit();
            activeGate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(650), () =>
        {
            pendingAttachments.Add(new RuAgent.Cli.Attachments.CliTextAttachment("draft.md", "draft remains", 13));
            window.Composer.Input.Text = "/model";
            loop.Submit();
            Assert.Single(pendingAttachments);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(800), () =>
        {
            window.Composer.Input.Text = "/retry";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(1000), () =>
        {
            window.Composer.Input.Text = "/retry";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(1300), () =>
        {
            Assert.Equal(["active", "message-1", "/model", "/retry", "message-2", "/retry"], seen);
            Assert.Single(pendingAttachments);
            app.RequestStop();
            return false;
        });

        app.Run(window);
        await loop.Completion;
    }

    [Fact]
    public void Enter_SubmitsExactlyOnce_AndClearsComposer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var submitted = new List<string>();
        loop.SetPromptHandler((line, _, _) =>
        {
            submitted.Add(line);
            return Task.CompletedTask;
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "xin chào";
            app.InjectKey(Key.Enter);
            Assert.Equal(["xin chào"], submitted);
            Assert.Equal(string.Empty, window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Esc_CancelsInFlightTurn()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var cancelled = false;
        var reachedAfterCancel = false;
        loop.SetPromptHandler(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "chạy lâu";
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(450), () =>
        {
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            reachedAfterCancel = true;
            Assert.True(cancelled);
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("esc interrupted", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        Assert.True(reachedAfterCancel, "Escape must cancel the active turn without quitting the chat.");
    }

    [Fact]
    public void Tab_CompletesKnownCommand()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "/mod";
            app.InjectKey(Key.Tab);
            Assert.Equal("/model ", window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void CtrlO_TogglesTheLastCollapsedBlock()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);
        window.Transcript.AppendCollapsed(
            "    │ · 2 more lines · ctrl+o",
            ["    │ + added line", "    │ - removed line"],
            TranscriptStyle.Tool);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.O.WithCtrl);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var expanded = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("+ added line", expanded);
            Assert.Contains("- removed line", expanded);
            app.InjectKey(Key.O.WithCtrl);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            var collapsed = string.Join('\n', TgSnapshot.Capture(app));
            Assert.DoesNotContain("+ added line", collapsed);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void AttachmentLabel_ShowsInHintsRow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetAttachmentLabelProvider(() => "[2 attachments] C:/Users/USER");

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            loop.RefreshAttachmentLabel();
            return false;
        });
        // The label is asserted on the next frame: SetIdle only marks the view dirty, and
        // the snapshot reads the driver's last drawn cells.
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.Contains("[2 attachments] C:/Users/USER", string.Join('\n', TgSnapshot.Capture(app)));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// Task 10's parked finding: the empty-prompt guard rejected an attachment-only turn,
    /// which classic mode allows. With attachments pending the empty line must submit; the
    /// prompt handler receives the empty string and composes the pending files.
    /// </summary>
    [Fact]
    public async Task Submit_AttachmentOnlyTurn_IsAllowed()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var submitted = new List<string>();
        loop.SetPromptHandler((line, _, _) =>
        {
            submitted.Add(line);
            return Task.CompletedTask;
        });
        loop.SetAttachmentCountProvider(() => 2);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            loop.Submit();
            Assert.Equal([string.Empty], submitted);
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    [Fact]
    public void Submit_EmptyPromptWithoutAttachments_IsIgnored()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var submitted = new List<string>();
        loop.SetPromptHandler((line, _, _) =>
        {
            submitted.Add(line);
            return Task.CompletedTask;
        });
        loop.SetAttachmentCountProvider(() => 0);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            loop.Submit();
            Assert.Empty(submitted);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// Task 11's parked finding: a full queue must refuse the new prompt like the classic
    /// type-ahead (`queue full · enter retries`) instead of silently dropping the oldest
    /// queued line, and the composer keeps the refused line so Enter can retry.
    /// </summary>
    [Fact]
    public async Task FullQueue_RefusesTheNewLine_AndQueuedLinesStillDrain()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        loop.SetPromptHandler(async (line, _, _) =>
        {
            seen.Add(line);
            if (line == "một")
            {
                await gate.Task;
            }
        });

        var queued = new[] { "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín" };
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "một";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            foreach (var line in queued)
            {
                window.Composer.Input.Text = line;
                loop.Submit();
            }

            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            window.Composer.Input.Text = "mười";
            loop.Submit();
            Assert.Equal("mười", window.Composer.Input.Text?.ToString());
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.Contains("queue full · enter retries", string.Join('\n', TgSnapshot.Capture(app)));
            gate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(1200), () =>
        {
            var expected = new List<string> { "một" };
            expected.AddRange(queued);
            Assert.Equal(expected, seen);
            Assert.DoesNotContain("mười", seen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    /// <summary>
    /// Task 14's parked finding: Stop must not release Completion while the cancelled turn's
    /// teardown still runs, and queued lines must be dropped instead of dispatching after a
    /// handover. The in-flight turn is held open, then released after Stop; Completion must
    /// only complete once that turn ended and the queued line must never run.
    /// </summary>
    [Fact]
    public async Task Stop_WaitsForTheRunningTurn_AndDropsQueuedLines()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        loop.SetPromptHandler(async (line, _, _) =>
        {
            seen.Add(line);
            if (line == "một")
            {
                await gate.Task;
            }
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "một";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            window.Composer.Input.Text = "hai";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            loop.Stop();
            Assert.False(loop.Completion.IsCompleted);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            gate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(900), () =>
        {
            Assert.True(loop.Completion.IsCompleted);
            Assert.Equal(["một"], seen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    /// <summary>
    /// Probe for the handover early-dispose path: the chat command disposes tgApp once when
    /// it leaves the TG frame and the using declaration disposes it again at scope exit, so
    /// a second Dispose must be a no-op.
    /// </summary>
    [Fact]
    public void ApplicationDispose_IsIdempotent()
    {
        var app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Dispose();
        app.Dispose();
    }

    /// <summary>Prompt recall retains insertion order and avoids consecutive duplicates.</summary>
    [Fact]
    public void History_ExposesRememberedPrompts_NewestLast()
    {
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.RememberForTests("một");
        loop.RememberForTests("hai");
        loop.RememberForTests("hai");

        Assert.Equal(["một", "hai"], loop.History);
    }

    /// <summary>
    /// M2: the outer session token is linked into the per-turn token, so a shutdown that
    /// never touches the window still cancels the in-flight turn.
    /// </summary>
    [Fact]
    public async Task OuterCancellation_CancelsTheInFlightTurn()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        using var outer = new CancellationTokenSource();
        var loop = new TgChatLoop(window, outer.Token);
        var cancelled = new TaskCompletionSource();
        loop.SetPromptHandler(async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "chạy lâu";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            outer.Cancel();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            Assert.True(cancelled.Task.IsCompleted);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    /// <summary>
    /// M2: the per-turn CTS is disposed once its handler settles, so a late Cancel (Escape
    /// after the turn, or the handover) must not throw on a disposed source.
    /// </summary>
    [Fact]
    public async Task Cancel_AfterTheTurnSettled_DoesNotThrow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "xong";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            loop.Cancel();
            loop.Stop();
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }

    [Fact]
    public void EnterDeliveredAsLineFeed_StillSubmits()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var seen = new List<string>();
        loop.SetPromptHandler((line, _, _) =>
        {
            seen.Add(line);
            return Task.CompletedTask;
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "lf";
            app.InjectKey(new Key((KeyCode)10));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            Assert.Equal(["lf"], seen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
