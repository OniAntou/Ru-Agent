using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

        /// <summary>Approval scope: "once" or "session" (external folders only).</summary>
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
        /// a non-terminal approval does not show an empty code box. The Always
        /// affordance appears only for external-directory asks; every other reason
        /// stays one-time because the server fails session scope closed for them.
        /// </summary>
        public void Show(string summary, string? command, bool allowSessionDirectory = false)
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

            AlwaysButton.Visibility = allowSessionDirectory ? Visibility.Visible : Visibility.Collapsed;
            SessionScopeHint.Visibility = allowSessionDirectory ? Visibility.Visible : Visibility.Collapsed;
            Panel.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Panel.Visibility = Visibility.Collapsed;
            CommandText.Text = string.Empty;
            CommandContainer.Visibility = Visibility.Collapsed;
            AlwaysButton.Visibility = Visibility.Collapsed;
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
            DenyButton.IsEnabled = enabled;
        }

        /// <summary>Moves focus onto Approve so a keyboard user can decide immediately.</summary>
        public void FocusDecision() => ApproveButton?.Focus(FocusState.Programmatic);

        private void OnApproveClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: true, scope: "once"));

        private void OnAlwaysClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: true, scope: "session"));

        private void OnDenyClick(object sender, RoutedEventArgs e) =>
            DecisionRequested?.Invoke(this, new AgentApprovalDecidedEventArgs(approved: false));
    }
}
