using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RuApp.Controls
{
    /// <summary>Which decision the user made on a pending approval.</summary>
    public sealed class AgentApprovalDecidedEventArgs : EventArgs
    {
        public AgentApprovalDecidedEventArgs(bool approved, string scope = "once")
        {
            Approved = approved;
            Scope = scope;
        }

        public bool Approved { get; }

        /// <summary>Approval scope: "once", "session" (external folders only), or "always" (remember as a project rule).</summary>
        public string Scope { get; }
    }

    /// <summary>
    /// The inline approval prompt: the reason, the terminal command being approved
    /// (when there is one), and the approve/deny decision.
    ///
    /// Extracted from ChatView so the prompt owns its own presentation and only
    /// reports the decision back. The view still decides when the prompt is shown
    /// and what it says.
    /// </summary>
    public sealed partial class AgentApprovalPanelControl : UserControl
    {
        /// <summary>Raised when the user approves or denies.</summary>
        public event EventHandler<AgentApprovalDecidedEventArgs>? DecisionRequested;

        public AgentApprovalPanelControl()
        {
            InitializeComponent();
        }

        public bool IsShown => Panel.Visibility == Visibility.Visible;

        public bool IsDecisionEnabled => ApproveButton.IsEnabled;

        /// <summary>Summary text currently presented, for tests and diagnostics.</summary>
        public string Summary => SummaryText.Text;

        /// <summary>Command currently presented, empty when the prompt has no command.</summary>
        public string Command => CommandText.Text;

        public bool IsCommandShown => CommandContainer.Visibility == Visibility.Visible;

        /// <summary>
        /// Presents one pending approval. A blank command hides the command block, so
        /// a non-terminal approval does not show an empty code box. The plain approve
        /// label follows the Host's session approval ledger: remembered for the
        /// session on every ask whose memory the evaluator honours, single-use on an
        /// external-directory ask (whose folder decisions are the separate buttons)
        /// and an explicit rule ask, which outranks remembered allows. The folder
        /// affordance appears only for external-directory asks because the server
        /// fails that command for every other reason; the remember affordance
        /// appears for every known ask reason that may become a project rule,
        /// including an external-directory ask (which remembers the folder).
        /// </summary>
        public void Show(
            string summary,
            string? command,
            bool allowSessionDirectory = false,
            bool allowRememberAlways = false,
            bool allowSessionMemory = true)
        {
            SummaryText.Text = summary ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(command))
            {
                CommandText.Text = command;
                CommandContainer.Visibility = Visibility.Visible;
            }
            else
            {
                CommandText.Text = string.Empty;
                CommandContainer.Visibility = Visibility.Collapsed;
            }

            bool allowOnce = allowSessionDirectory || !allowSessionMemory;
            ApproveButtonLabel.Text = allowOnce
                ? "Allow once"
                : "Allow (remember for session)";
            AutomationProperties.SetName(
                ApproveButton,
                allowSessionDirectory
                    ? "Allow this folder call once"
                    : allowSessionMemory
                        ? "Allow and remember for this chat session"
                        : "Allow this call once");

            AlwaysButton.Visibility = allowSessionDirectory ? Visibility.Visible : Visibility.Collapsed;
            RememberButton.Visibility = allowRememberAlways ? Visibility.Visible : Visibility.Collapsed;
            RememberButtonLabel.Text = allowSessionDirectory
                ? "Remember folder for project"
                : "Always allow";
            AutomationProperties.SetName(
                RememberButton,
                allowSessionDirectory
                    ? "Remember this folder as a project rule"
                    : "Always allow and remember this action as a project rule");

            if (allowSessionDirectory && allowRememberAlways)
            {
                SessionScopeHint.Text = "This folder can stay open for this chat session, or be remembered for this project.";
                SessionScopeHint.Visibility = Visibility.Visible;
            }
            else if (allowSessionDirectory)
            {
                SessionScopeHint.Text = "This folder stays open for this chat session only.";
                SessionScopeHint.Visibility = Visibility.Visible;
            }
            else if (allowRememberAlways)
            {
                SessionScopeHint.Text = "Always allow remembers this action pattern for this project.";
                SessionScopeHint.Visibility = Visibility.Visible;
            }
            else
            {
                SessionScopeHint.Visibility = Visibility.Collapsed;
            }

            Panel.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Panel.Visibility = Visibility.Collapsed;
            CommandText.Text = string.Empty;
            CommandContainer.Visibility = Visibility.Collapsed;
            AlwaysButton.Visibility = Visibility.Collapsed;
            RememberButton.Visibility = Visibility.Collapsed;
            SessionScopeHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Enables or disables the decision while a request is in flight. The buttons
        /// stay visible: hiding them mid-request would read as the prompt vanishing.
        /// </summary>
        public void SetDecisionEnabled(bool enabled)
        {
            ApproveButton.IsEnabled = enabled;
            AlwaysButton.IsEnabled = enabled;
            RememberButton.IsEnabled = enabled;
            DenyButton.IsEnabled = enabled;
        }

        /// <summary>
        /// Moves focus onto Deny so a keyboard user is on the safe button, not the
        /// dangerous one.
        /// </summary>
        /// <remarks>
        /// This used to land on Approve, on the reasoning that a keyboard user should be
        /// able to decide immediately. In an app that runs arbitrary shell commands that
        /// reasoning has the wrong sign: Enter is the reflex for dismissing something, and
        /// the button under it would be the one that executes a command the user has not
        /// read. Landing on Deny means a reflexive Enter answers "no" and the agent asks
        /// again, which costs a round trip. Landing on Approve means a reflexive Enter
        /// runs the command.
        ///
        /// Deny is not the only way out either. Escape answers Deny from anywhere in the
        /// panel, and Tab reaches Approve in one press, so nothing is hidden from a user
        /// who means to approve — it just is not what happens to the user who does not
        /// look.
        /// </remarks>
        public void FocusDecision() => DenyButton?.Focus(FocusState.Programmatic);

        /// <summary>
        /// Escape, from any part of the panel, answers Deny.
        /// </summary>
        /// <remarks>
        /// Hooked here rather than on the chat surface so the prompt answers Escape in
        /// its own voice wherever its focus happens to be. It bubbles from the buttons,
        /// so it does not matter which of the three the user reached with Tab.
        /// </remarks>
        private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape || !IsDecisionEnabled)
            {
                return;
            }

            e.Handled = true;
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: false));
        }

        private void OnApproveClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: true, scope: "once"));

        private void OnAlwaysClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: true, scope: "session"));

        private void OnRememberClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: true, scope: "always"));

        private void OnDenyClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: false));
    }
}
