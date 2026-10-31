using RuAgent.Cli.TerminalGui;


namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>
        /// Runs the native session host. Each page binds its keys and presentation to
        /// the same conversation controller that the line adapter uses.
        /// </summary>
        private Task RunTerminalGuiChatAsync() => new TuiSessionManager(this).RunAsync();

        private TgChatLoop CreateTerminalGuiLoop()
        {
            var loop = new TgChatLoop(tgWindow!, cancellationToken);
            if (multiSessions is not null) loop.SetSessionCommandHandler(line => multiSessions.TryHandleCommand(this, line));
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
