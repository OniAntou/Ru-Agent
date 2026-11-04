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
            var loop = CreateTerminalGuiLoop();
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
            var timer = tgApp.AddTimeout(TimeSpan.FromMilliseconds(Tui.TuiHeader.MeteorFrameMilliseconds), () =>
            {
                if (Volatile.Read(ref stopping) != 0) return false;
                TickSession(clock.Elapsed.TotalSeconds);
                return true;
            });
            try { if (Volatile.Read(ref stopping) == 0) tgApp.Run(tgWindow!); }
            finally
            {
                StopTerminalGuiChat();
                if (startup is not null) tgApp.RemoveTimeout(startup);
                if (timer is not null) tgApp.RemoveTimeout(timer);
                // UI completion has a bounded drain. Accepted creates and attachment
                // handlers still own resources until their settlement completes.
                await loop.Completion.ConfigureAwait(false);
                await loop.WaitForShutdownSettlementAsync().ConfigureAwait(false);
                loop.SetViewActive(null);
                submissions.ClearPending();
                stopTerminalGui = null;
            }
        }

        private Action? stopTerminalGui;
        private void StopTerminalGuiChat() => stopTerminalGui?.Invoke();

        private TgChatLoop CreateTerminalGuiLoop()
        {
            var loop = new TgChatLoop(tgWindow!, cancellationToken);
            loop.SetImmediateCommandHandler(line =>
            {
                if (!line.Trim().Split(' ', 2)[0].Equals("/exit", StringComparison.OrdinalIgnoreCase)) return false;
                tgWindow!.Composer.Input.Text = string.Empty;
                StopTerminalGuiChat();
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
