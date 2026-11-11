using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Automation;
using RuAgent.Hosting;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using RuApp.Views;

namespace RuApp
{
    public sealed partial class MainWindow
    {
        private async Task ShowNoticeAsync(string title, string content)
        {
            if (ShellRoot.XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = ShellRoot.XamlRoot,
            };
            await ContentDialogGate.ShowAsync(dialog);
        }

        /// <summary>
        /// One bounded status line for a session action that failed inside an
        /// async void handler: the action must not die silently, and the failure
        /// must not take the process down.
        /// </summary>
        private void SetSessionActionError(string message)
        {
            Shell.ReportError(message);
        }

        private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await DeleteSessionAsync(sender);
            }
            catch (Exception ex)
            {
                // async void: the confirm and error dialogs can both throw, and an
                // unhandled exception here would take the process down.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Session delete failed: {ex.Message}");
                SetSessionActionError("Could not delete the chat");
            }
        }

        private async Task DeleteSessionAsync(object sender)
        {
            if (sender is not FrameworkElement { Tag: string sessionId } || _apiService is null) return;

            var prompts = new SessionDeletionDialogs(this);
            var result = await Sidebar.DeleteAsync(sessionId, _chatView?.CurrentSessionId, prompts);
            if (!result.IsRemoved) return;

            _chatView?.ForgetSidePanelState(sessionId);
            switch (result.FollowUp)
            {
                case SessionDeletionFollowUp.OpenSession open:
                    SidebarSessionList.SelectedItem = open.Session;
                    if (_chatView != null)
                    {
                        await _chatView.OpenSessionAsync(open.Session.Id, open.Session.Title);
                    }

                    break;
                case SessionDeletionFollowUp.StartNewChat:
                    OnNewChatClick(this, new RoutedEventArgs());
                    break;
            }

            // A partial result still deleted the chat: only its artifacts are left, so
            // the offer to retry the cleanup never calls the chat undeleted.
            if (result.IsPartial)
            {
                await Sidebar.RetryCleanupAsync(sessionId, result.PartialMessage, prompts);
            }
        }

        /// <summary>The delete confirmations and notices, as content dialogs on this window.</summary>
        private sealed class SessionDeletionDialogs(MainWindow window) : ISessionDeletionPrompts
        {
            public async Task<bool> ConfirmDeleteAsync()
            {
                if (window.ShellRoot.XamlRoot is null) return true;
                var dialog = new ContentDialog
                {
                    Title = "Confirm delete chat",
                    Content = "Are you sure you want to permanently delete this chat?",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = window.ShellRoot.XamlRoot,
                };
                return await ContentDialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
            }

            public Task ShowDeleteFailedAsync(string? message) => window.ShowNoticeAsync(
                "Could not delete chat",
                message ?? "Backend failed to delete this chat. Please try again later.");

            public async Task<bool> OfferCleanupRetryAsync(string? message)
            {
                if (window.ShellRoot.XamlRoot is null) return false;
                var dialog = new ContentDialog
                {
                    Title = "Chat deleted, cleanup incomplete",
                    Content = (string.IsNullOrWhiteSpace(message)
                            ? "The chat was deleted, but its leftover artifacts could not be removed."
                            : message)
                        + "\n\nRetry the cleanup now?",
                    PrimaryButtonText = "Retry cleanup",
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = window.ShellRoot.XamlRoot,
                };
                return await ContentDialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
            }

            public Task ShowCleanupUnconfirmedAsync() => window.ShowNoticeAsync(
                "Cleanup not confirmed",
                "The chat was deleted. The cleanup ran again, but the host could not confirm the artifacts were removed."
                    + "\n\nLeftover artifacts are reclaimed automatically when they expire.");
        }

        private void SessionItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid row) { _sessionRowInteraction.GetOrCreateValue(row).PointerInside = true; UpdateSessionRowActions(row); }
        }

        private void SessionItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid row) { _sessionRowInteraction.GetOrCreateValue(row).PointerInside = false; UpdateSessionRowActions(row); }
        }

        /// <summary>
        /// Reveals the row's … and delete actions in place of the clock at the
        /// right edge, and restores the clock on leave.
        /// </summary>
        /// <remarks>
        /// The clock is collapsed rather than dimmed: it is wider than the two 22px
        /// controls together, so leaving it visible would paint the time underneath
        /// them. Everything is right-aligned in one cell, so swapping them does not
        /// move anything else in the row.
        ///
        /// Hit testing is toggled with the opacity. A control that stays invisible
        /// but still accepts clicks would turn a stray click on the timestamp into
        /// a menu open or a delete. Both controls stay tab stops so keyboard users
        /// reach the row menu and the delete without a pointer; there is no
        /// right-click path anymore.
        /// </remarks>
        private static void SetSessionRowActionsVisible(object sender, bool visible)
        {
            if (sender is not Grid grid)
            {
                return;
            }

            var cell = grid.Children.OfType<Grid>().FirstOrDefault();
            if (cell is null)
            {
                return;
            }

            if (cell.FindName("SessionMoreButton") is Button moreButton)
            {
                Services.UiMotion.FadeOpacity(moreButton, visible ? 1.0 : 0.0);
                moreButton.IsHitTestVisible = visible;
            }

            if (cell.FindName("SessionDeleteButton") is Button deleteButton)
            {
                Services.UiMotion.FadeOpacity(deleteButton, visible ? 1.0 : 0.0);
                deleteButton.IsHitTestVisible = visible;
            }

            if (cell.FindName("SessionTimeText") is TextBlock clock)
            {
                clock.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>
        /// Opens one session's token report, centred over the shell.
        ///
        /// The report used to be a flyout pinned to the row, which is where it was
        /// asked for and the worst place to read it: 268px wide beside a list, with
        /// three figures per run that wanted tabular space. It reads as a panel, so it
        /// opens as one.
        /// </summary>
        private async void OnSessionUsageClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await ShowSessionUsageAsync(sender);
            }
            catch (Exception ex)
            {
                // async void: a failed load must leave a bounded status instead of
                // an unhandled exception (and a stuck "open" overlay state).
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Session usage failed: {ex.Message}");
                CloseSessionUsage();
                SetSessionActionError("Could not load the session usage");
            }
        }

        private async Task ShowSessionUsageAsync(object sender)
        {
            if (sender is not FrameworkElement item
                || item.Tag is not string sessionId
                || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            // The id always comes off the clicked element; the title is only decoration
            // for the panel. A hover button carries the row as its DataContext, but a
            // flyout item does not reliably inherit one, so the list is the fallback.
            var title = (item.DataContext as SessionInfo)?.Title
                ?? Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))?.Title;

            await OpenSessionUsageAsync(sessionId, title);
        }

        private bool _isSessionUsageOpen;
        private object? _sessionUsagePreviousFocus;

        private async Task OpenSessionUsageAsync(string sessionId, string? sessionTitle)
        {
            if (_apiService is null)
            {
                return;
            }

            _isSessionUsageOpen = true;
            SessionUsageOverlay.Visibility = Visibility.Visible;
            Services.UiMotion.Enter(SessionUsageOverlay);
            ApplySessionUsageCardSize();
            _sessionUsagePreviousFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement();
            CloseSessionUsageButton?.Focus(FocusState.Programmatic);

            // The catalog prices the runs. It is read here rather than fetched by the
            // panel: the panel is not the picker, and a second catalog read would be a
            // second request for a list the app already holds. A catalog that is
            // missing, failed or stale prices nothing, and the panel says so rather
            // than reporting the session as free.
            await SessionUsagePanelControl.LoadAsync(
                _apiService,
                sessionId,
                sessionTitle,
                _chatView?.ProviderCatalogState,
                _chatView?.ContextReadingFor(sessionId));
        }

        private void CloseSessionUsage()
        {
            if (!_isSessionUsageOpen)
            {
                return;
            }

            _isSessionUsageOpen = false;

            // The overlay leaves with a fade; the collapse and the focus hand-back
            // follow it, unless the panel was reopened while it faded.
            Services.UiMotion.Exit(SessionUsageOverlay, () =>
            {
                if (_isSessionUsageOpen)
                {
                    return;
                }

                SessionUsageOverlay.Visibility = Visibility.Collapsed;
                if (_sessionUsagePreviousFocus is Control previousFocus)
                {
                    previousFocus.Focus(FocusState.Programmatic);
                }

                _sessionUsagePreviousFocus = null;
            });
        }

        private void OnSessionUsageOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                ApplySessionUsageCardSize();
            }
        }

        /// <summary>
        /// The card follows the window's width and stops growing once it would
        /// overflow the window's height.
        /// </summary>
        /// <remarks>
        /// The height is deliberately not set here. A card pinned to a fixed height
        /// that its content does not fill leaves a band of bare surface below the
        /// footer, which reads as a rendering fault rather than as space. The panel
        /// is read in one await and rendered in one pass, so letting the height
        /// follow the content does not make the card jitter while rows arrive.
        /// </remarks>
        private void ApplySessionUsageCardSize()
        {
            if (SessionUsageOverlay.ActualWidth <= 0 || SessionUsageOverlay.ActualHeight <= 0)
            {
                return;
            }

            SessionUsageCard.Width = Math.Min(560, Math.Max(360, SessionUsageOverlay.ActualWidth - 48));
            SessionUsageCard.MaxHeight = Math.Max(320, SessionUsageOverlay.ActualHeight - 40);
        }

        private void OnCloseSessionUsageClick(object sender, RoutedEventArgs e) => CloseSessionUsage();

        private void OnSessionUsageBackdropPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
            CloseSessionUsage();

        private void OnSessionUsageCardPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Swallow the press so a click inside the card does not reach the backdrop
            // behind it and close the panel the reader is working in.
        }
    }
}
