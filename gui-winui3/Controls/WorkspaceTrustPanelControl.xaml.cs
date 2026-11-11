using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The workspace trust prompt as a card in the chat flow. It only reports the user's
    /// choice; the view decides when the card shows, and the Host owns the decision.
    /// </summary>
    public sealed partial class WorkspaceTrustPanelControl : UserControl
    {
        /// <summary>Raised with true to trust the workspace and false to decline.</summary>
        public event EventHandler<bool>? DecisionRequested;

        public WorkspaceTrustPanelControl()
        {
            InitializeComponent();
        }

        public bool IsShown => Panel.Visibility == Visibility.Visible;

        /// <summary>Presents one trust prompt and puts the keyboard on Cancel.</summary>
        public void Show(WorkspaceTrustPromptPolicy.Prompt prompt)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            TitleText.Text = prompt.Title;
            ContentText.Text = prompt.Content;
            PathText.Text = prompt.WorkspaceText;
            AutomationProperties.SetName(PathText, $"Workspace: {prompt.WorkspaceText}");
            ApproveLabel.Text = prompt.PrimaryButtonText;
            AutomationProperties.SetName(ApproveButton, $"{prompt.PrimaryButtonText}, trust this workspace");
            WarningText.Text = prompt.ScopeWarning ?? string.Empty;
            WarningRow.Visibility = prompt.ScopeWarning is null ? Visibility.Collapsed : Visibility.Visible;
            SetDecisionEnabled(true);
            UiMotion.SetVisible(Panel, true);
            FocusDecision();
        }

        public void Hide()
        {
            Panel.Visibility = Visibility.Collapsed;
            TitleText.Text = string.Empty;
            ContentText.Text = string.Empty;
            PathText.Text = string.Empty;
            WarningText.Text = string.Empty;
            WarningRow.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Focus goes to Cancel, not Approve: Enter is the reflex for dismissing something,
        /// and the button under it must be the one that grants nothing.
        /// </summary>
        public void FocusDecision() => CancelButton?.Focus(FocusState.Programmatic);

        private void SetDecisionEnabled(bool enabled)
        {
            ApproveButton.IsEnabled = enabled;
            CancelButton.IsEnabled = enabled;
        }

        // Escape, from anywhere in the card, declines.
        private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape || !CancelButton.IsEnabled)
            {
                return;
            }

            e.Handled = true;
            Decide(false);
        }

        private void OnApproveClick(object sender, RoutedEventArgs e) => Decide(true);

        private void OnCancelClick(object sender, RoutedEventArgs e) => Decide(false);

        // One answer per prompt: the buttons go dead until the next Show.
        private void Decide(bool approved)
        {
            SetDecisionEnabled(false);
            DecisionRequested?.Invoke(this, approved);
        }
    }
}
