using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// The folder a run's session works in: the Host's session record first, then the
        /// exact path this app sent when it created the run. Never the composer's current
        /// default, which may have changed since the run was created.
        /// </summary>
        private string? ResolveRunWorkspacePath(string sessionId)
        {
            if (SessionWorkspaceResolver?.Invoke(sessionId) is { Length: > 0 } hostPath) return hostPath;
            return _requestedWorkspaceBySession.TryGetValue(sessionId, out var requested) ? requested : null;
        }

        private static UIElement BuildTrustPromptContent(WorkspaceTrustPromptPolicy.Prompt prompt)
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = prompt.Content, TextWrapping = TextWrapping.Wrap });
            var path = new TextBlock
            {
                Text = prompt.WorkspaceText,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = (FontFamily)Application.Current.Resources["RuMonoFontFamily"],
            };
            AutomationProperties.SetName(path, $"Workspace: {prompt.WorkspaceText}");
            panel.Children.Add(new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = Services.UiCornerRadius.Control,
                Background = (Brush)Application.Current.Resources["RuAccentSoftBrush"],
                Child = path,
            });
            if (prompt.ScopeWarning is { } warning)
            {
                // Ink text beside a warning glyph: the warning colour fails AA as body
                // text on the light theme, so it marks the line instead of painting it.
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new FontIcon
                {
                    Glyph = "",
                    FontSize = 14,
                    Foreground = (Brush)Application.Current.Resources["RuWarningBrush"],
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                var text = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                panel.Children.Add(row);
            }

            return panel;
        }

        private async void ShowWorkspaceTrustDialogAsync()
        {
            var controller = _agentController;
            var run = controller?.CurrentRun;
            if (controller is null
                || run is null
                || !string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                || !controller.NeedsWorkspaceTrust
                || XamlRoot is null
                || !RunPanel.TryBeginTrustPrompt(controller, run.RunId, run.SessionId, DateTimeOffset.UtcNow))
            {
                return;
            }

            _sessionHistoryPresenter.SetActionPending(true);
            UpdateAgentInlineState();

            Exception? failure = null;
            try
            {
                var prompt = WorkspaceTrustPromptPolicy.For(
                    _workspaceTrustMemory.IsReTrust(_agentWorkspace, run.DisplayIdentityHash),
                    ResolveRunWorkspacePath(run.SessionId));
                var dialog = new ContentDialog
                {
                    Title = prompt.Title,
                    Content = BuildTrustPromptContent(prompt),
                    PrimaryButtonText = prompt.PrimaryButtonText,
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };

                ContentDialogResult result = await ContentDialogGate.ShowAsync(dialog);
                bool isCurrentRequest = ReferenceEquals(_agentController, controller)
                    && RunPanel.OwnsTrustPrompt(controller, run.RunId, run.SessionId)
                    && controller.CurrentRun is { } currentRun
                    && string.Equals(currentRun.RunId, run.RunId, StringComparison.Ordinal)
                    && string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal)
                    && controller.NeedsWorkspaceTrust;
                if (!isCurrentRequest)
                {
                    return;
                }

                if (result == ContentDialogResult.Primary)
                {
                    await controller.TrustAsync(approve: true);
                    // Only an accepted approval reaches this line: the client throws on a
                    // rejected decision, so the memory cannot claim a trust the Host never recorded.
                    _workspaceTrustMemory.Remember(_agentWorkspace, run.DisplayIdentityHash);
                    Composer.ForgetAcceptedSend(run.SessionId);
                }
                else
                {
                    await controller.TrustAsync(approve: false);
                    // The run ends without starting; the message it carried goes back to
                    // the composer instead of vanishing (B7).
                    if (string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                        && Composer.RestoreDeclinedSend(run.SessionId, ChatInputTextBox?.Text ?? string.Empty))
                    {
                        ShowAgentInlineStatus(DeclinedTrustStatus);
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // A prompt that could not open (another dialog was up) is retried by the
                // next monitor refresh after a short cooldown, not counted as answered.
                bool couldNotOpen = failure is not null && ContentDialogGate.IsDialogConflict(failure);
                bool ownsGuard = RunPanel.EndTrustPrompt(controller, run.RunId, run.SessionId, couldNotOpen, DateTimeOffset.UtcNow);
                if (!RunPanel.IsTrustPromptOpen)
                {
                    _sessionHistoryPresenter.SetActionPending(false);
                    UpdateAgentInlineState();
                }

                if (failure is not null && ownsGuard)
                {
                    if (couldNotOpen)
                    {
                        ShowAgentInlineStatus("Could not open the workspace trust prompt. Close any open dialog; the prompt will reappear.");
                    }
                    else
                    {
                        ShowAgentError(failure);
                    }
                }
            }
        }
    }
}
