namespace RuApp.Controls
{
    /// <summary>
    /// The Terminal tab's host: a real interactive shell (ConPTY plus xterm.js
    /// in WebView2) for the user, pointed at the session workspace. The Agent can
    /// neither see nor call it, and the Broker stays the only execution authority
    /// for model-visible tool calls.
    /// </summary>
    public sealed partial class AgentSidePanelControl
    {
        private string? _terminalWorkingDirectory;

        /// <summary>
        /// Points the terminal at the session workspace; a different directory
        /// restarts the shell the next time the tab is shown.
        /// </summary>
        public void SetTerminalWorkingDirectory(string? path) => _terminalWorkingDirectory = path;

        /// <summary>Starts or focuses the terminal when its tab opens.</summary>
        public void RevealTerminal() => TerminalTabView.EnsureStarted(_terminalWorkingDirectory);

        /// <summary>Ends the shell; called when the app closes.</summary>
        public void ShutdownTerminal() => TerminalTabView.Shutdown();
    }
}
