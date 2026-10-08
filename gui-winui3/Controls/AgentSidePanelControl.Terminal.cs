namespace RuApp.Controls
{
    /// <summary>
    /// The Terminal tab's live stream: the same session terminal output the
    /// composer panel shows, hosted here as a second bounded block list. The view
    /// forwards every stream event to both instances.
    /// </summary>
    public sealed partial class AgentSidePanelControl
    {
        public void AppendTerminalOutput(string executionId, string text) =>
            TerminalTabPanel.AppendOutput(executionId, text);

        public void CompleteTerminalBlock(string? executionId, bool failed) =>
            TerminalTabPanel.CompleteBlock(executionId, failed);

        public void SettleTerminalBlocks() => TerminalTabPanel.SettleRunningBlocks();

        public void NoteTerminalToolLabel(string? toolLabel) => TerminalTabPanel.NoteToolLabel(toolLabel);

        public void ClearTerminalBlocks() => TerminalTabPanel.Clear();

        /// <summary>Pins the terminal tab's scroll to its newest output.</summary>
        public void RevealTerminal()
        {
            if (TerminalTabPanel.TerminalBlocks.Count > 0)
            {
                TerminalTabPanel.Show();
            }
        }
    }
}
