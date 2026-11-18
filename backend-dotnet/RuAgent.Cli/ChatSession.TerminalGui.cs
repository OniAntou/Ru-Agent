using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;


namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>
        /// Runs one native conversation controller. Saved sessions replace its state;
        /// opening history never leaves another worker running behind the view.
        /// </summary>
        private async Task RunTerminalGuiChatAsync()
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = lifetime.Token;
            Task? sessionLoad = null;
            var loop = CreateTerminalGuiLoop(() => sessionLoad is { IsCompleted: false });
            var stopping = 0;
            stopTerminalGui = () =>
            {
                if (Interlocked.Exchange(ref stopping, 1) != 0) return;
                managedShutdown = true;
                _ = attachmentTasks.StopAsync();
                loop.Stop();
                lifetime.Cancel();
                try { tgApp!.Invoke(() => tgApp.RequestStop()); }
                catch (NotInitializedException) { }
            };
            using var registration = cancellationToken.Register(StopTerminalGuiChat);
            if (!string.IsNullOrWhiteSpace(cmd.SessionId))
                sessionLoad = Task.Run(LoadInitialSessionAsync);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var startup = tgApp!.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
            {
                if (Volatile.Read(ref stopping) != 0) return false;
                tgWindow!.FocusSessionInput();
                if (runSession.FollowableRunId is not null && submissions.Count == 0
                    && tgWindow.Composer.Input.Text.Length == 0)
                {
                    tgWindow.Composer.Input.Text = "Continue";
                    loop.Submit();
                }
                return false;
            });
            // The session clock ticks at frame rate only while something animates. A settled
            // welcome or an idle conversation drops to the quiet interval, so an untouched
            // session does not wake the CPU 55 times a second.
            var animatingInterval = TimeSpan.FromMilliseconds(Tui.TuiHeader.MeteorFrameMilliseconds);
            var quietInterval = TimeSpan.FromMilliseconds(QuietTickMilliseconds);
            var interval = animatingInterval;
            object? timer = null;
            bool OnSessionTick()
            {
                if (Volatile.Read(ref stopping) != 0) return false;
                TickSession(clock.Elapsed.TotalSeconds);
                var next = IsSessionQuiet() ? quietInterval : animatingInterval;
                if (next == interval) return true;
                interval = next;
                timer = tgApp.AddTimeout(next, OnSessionTick);
                return false;
            }
            timer = tgApp.AddTimeout(animatingInterval, OnSessionTick);
            // Any input or resize lets the welcome animate for another cycle.
            void WakeOnKey(object? sender, Terminal.Gui.Input.Key key) => tgWindow!.Landing.Wake();
            void WakeOnMouse(object? sender, Terminal.Gui.Input.Mouse mouse) => tgWindow!.Landing.Wake();
            void WakeOnResize(object? sender, Terminal.Gui.App.EventArgs<System.Drawing.Rectangle> args) =>
                tgWindow!.Landing.Wake();
            tgApp.Keyboard.KeyDown += WakeOnKey;
            tgApp.Mouse.MouseEvent += WakeOnMouse;
            tgApp.ScreenChanged += WakeOnResize;
            try { if (Volatile.Read(ref stopping) == 0) tgApp.Run(tgWindow!); }
            finally
            {
                StopTerminalGuiChat();
                tgApp.Keyboard.KeyDown -= WakeOnKey;
                tgApp.Mouse.MouseEvent -= WakeOnMouse;
                tgApp.ScreenChanged -= WakeOnResize;
                if (startup is not null) tgApp.RemoveTimeout(startup);
                if (timer is not null) tgApp.RemoveTimeout(timer);
                // UI completion has a bounded drain. Accepted creates and attachment
                // handlers still own resources until their settlement completes.
                await loop.Completion.ConfigureAwait(false);
                await loop.WaitForShutdownSettlementAsync().ConfigureAwait(false);
                if (sessionLoad is not null)
                {
                    try { await sessionLoad.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                }
                loop.SetViewActive(null);
                submissions.ClearPending();
                stopTerminalGui = null;
            }
        }

        /// <summary>Session clock interval while nothing on screen animates.</summary>
        private const int QuietTickMilliseconds = 50;

        /// <summary>
        /// Nothing needs frame-rate ticks: the welcome is settled or hidden and no run shows a
        /// live activity row or hint wave.
        /// </summary>
        private bool IsSessionQuiet()
        {
            var landing = tgWindow!.Landing;
            var welcomeStill = !landing.Visible || landing.IsSettled;
            return welcomeStill && sessionRenderer.Status.State is Tui.TuiRunState.Idle or Tui.TuiRunState.Failed;
        }

        private Action? stopTerminalGui;
        private void StopTerminalGuiChat() => stopTerminalGui?.Invoke();

        private TgChatLoop CreateTerminalGuiLoop(Func<bool> openingSession)
        {
            var loop = new TgChatLoop(tgWindow!, cancellationToken);
            loop.SetImmediateCommandHandler(line =>
            {
                if (line.Trim().Split(' ', 2)[0].Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    tgWindow!.Composer.Input.Text = string.Empty;
                    StopTerminalGuiChat();
                    return true;
                }
                if (!openingSession()) return false;
                tgWindow!.Hints.ShowFeedback("loading session history");
                return true;
            });
            tgLoop = loop;
            ConfigureChatEngine(loop);
            return loop;
        }

        private void TickSession(double elapsedSeconds)
        {
            tgWindow!.TickWelcome();
            sessionRenderer.Width = ChatRenderWidth();
            var transcriptWidth = tgWindow.Transcript.Viewport.Width;
            if (transcriptWidth > 0)
            {
                var state = sessionRenderer.Status.State;
                if (state is Tui.TuiRunState.Idle or Tui.TuiRunState.Failed)
                {
                    tgWindow.Transcript.ClearLive();
                }
                else
                {
                    // The live row is the running form of the settled tool row that
                    // will replace it: the timer owns its phase, the writer clears
                    // it the moment the permanent row lands.
                    tgWindow.Transcript.UpdateLive(sessionRenderer.PresentActivity(transcriptWidth));
                }
            }

            tgWindow.Hints.SetWaveClock(elapsedSeconds);
        }
    }
}
