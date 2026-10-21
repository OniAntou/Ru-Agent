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
using RuAgent.SessionInterchange;
using RuApp.Models;
using RuApp.Services;
using RuApp.Views;

namespace RuApp
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Hands a session to another coding agent, in that agent's own format and
        /// directory.
        /// </summary>
        /// <remarks>
        /// The two menu items differ only by the format they name, so the harness is read
        /// off the clicked item rather than duplicated into a second handler that would
        /// drift from this one.
        /// </remarks>
        private async void OnExportSessionMenuClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await ExportSessionMenuAsync(sender);
            }
            catch (Exception ex)
            {
                // async void: an unhandled exception here tears the process down
                // (a dialog opened while another is up is the common case).
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Session export failed: {ex.Message}");
                SetSessionActionError("Could not export the session");
            }
        }

        private async Task ExportSessionMenuAsync(object sender)
        {
            if (sender is not FrameworkElement item
                || item.Tag is not string sessionId
                || string.IsNullOrWhiteSpace(sessionId)
                || _apiService is null)
            {
                return;
            }

            var harness = item.Name switch
            {
                "ExportCodexItem" => "codex",
                _ => "claude-code",
            };

            var outcome = await _apiService.ExportSessionAsync(sessionId, harness);
            if (ShellRoot.XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = outcome.Ok ? "Session exported" : "Could not export this session",
                Content = outcome.Ok
                    ? string.Join('\n', outcome.Paths) + "\n\nRestart that agent so it picks the session up."
                    : outcome.Error ?? "The export failed.",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = ShellRoot.XamlRoot,
            };
            await ContentDialogGate.ShowAsync(dialog);
        }

        /// <summary>
        /// Brings another agent's session file in as a new Ru session.
        /// </summary>
        /// <remarks>
        /// The file chooses its own format: the name says which agent wrote it. When the
        /// name says nothing, this asks instead of guessing — a guessed format would read
        /// the file as the wrong thing and then refuse it, which reads like the file's
        /// fault.
        /// </remarks>
        private async void OnImportSessionMenuClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await ImportSessionMenuAsync();
            }
            catch (Exception ex)
            {
                // async void: the picker and the dialog can both throw (a dialog
                // opened while another is up), and an unhandled exception would
                // take the process down.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Session import failed: {ex.Message}");
                SetSessionActionError("Could not import the session");
            }
        }

        private async Task ImportSessionMenuAsync()
        {
            if (_apiService is null || ShellRoot.XamlRoot is null)
            {
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };

            // Only the JSONL formats the desktop app can decode. opencode keeps its
            // sessions inside a SQLite database, which this app cannot read; offering the
            // .db file here would advertise an import that always fails, so the hint below
            // points at the CLI instead.
            picker.FileTypeFilter.Add(".jsonl");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var harness = SessionFileLayout.HarnessFromFileName(file.Path);
            if (harness is null)
            {
                await ShowNoticeAsync(
                    "Cannot tell which agent wrote this file",
                    $"Ru recognises {SessionFileLayout.ClaudeCodeId} transcripts, named after their session id, {SessionFileLayout.CodexId} rollouts, named rollout-<when>-<thread>, and Antigravity's transcript.jsonl. opencode stores sessions in a SQLite database the desktop app cannot read: import those with 'ru agent session import <opencode.db> --harness opencode'.");
                return;
            }

            // The size check and the read share one try: a file that disappears between
            // the picker and the stat must end as a bounded dialog, not as an exception
            // thrown out of an async void handler.
            ulong? size = null;
            string? content = null;
            try
            {
                size = (await file.GetBasicPropertiesAsync()).Size;
                if (size <= (ulong)SessionInterchangeLimits.MaxImportContentBytes)
                {
                    content = await Windows.Storage.FileIO.ReadTextAsync(file);
                }
            }
            catch (Exception ex)
            {
                var message = ex.Message.Length > 400 ? ex.Message[..400] + "…" : ex.Message;
                await ShowNoticeAsync("Could not read this file", message);
                return;
            }

            if (content is null)
            {
                await ShowNoticeAsync(
                    "Session file too large",
                    $"This file is {size} bytes. Imports are limited to {SessionInterchangeLimits.MaxImportContentBytes / (1024 * 1024)} MiB.");
                return;
            }

            var outcome = await _apiService.ImportSessionAsync(harness, content);
            if (!outcome.Ok)
            {
                await ShowNoticeAsync("Could not import this session", outcome.Error ?? "The import failed.");
                return;
            }

            await LoadSessionsAsync();

            await ShowNoticeAsync(
                "Session imported",
                outcome.DroppedEntries > 0
                    ? $"{outcome.Messages} messages came across as a new session. {outcome.DroppedEntries} entries stayed behind: Ru's log holds turns, and another agent's tool traffic belongs to runs."
                    : $"{outcome.Messages} messages came across as a new session.");
        }

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
            StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
            StatusText.Text = message;
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
            if (sender is FrameworkElement btn && btn.Tag is string sessionId && _apiService != null)
            {
                if (ShellRoot.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Confirm delete chat",
                        Content = "Are you sure you want to permanently delete this chat?",
                        PrimaryButtonText = "Delete",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = ShellRoot.XamlRoot
                    };

                    var result = await ContentDialogGate.ShowAsync(dialog);
                    if (result != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                var outcome = await _apiService.DeleteSessionAsync(sessionId);
                if (!outcome.Deleted && !outcome.Partial)
                {
                    if (ShellRoot.XamlRoot != null)
                    {
                        var errorDialog = new ContentDialog
                        {
                            Title = "Could not delete chat",
                            Content = outcome.Message ?? "Backend failed to delete this chat. Please try again later.",
                            CloseButtonText = "Close",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = ShellRoot.XamlRoot
                        };
                        await ContentDialogGate.ShowAsync(errorDialog);
                    }

                    return;
                }

                bool wasActive = string.Equals(_chatView?.CurrentSessionId, sessionId, StringComparison.OrdinalIgnoreCase);

                if (outcome.Deleted)
                {
                    _sessionDrafts.Remove(sessionId);
                    _sidebarOrganization.Remove(sessionId);
                }
                _chatView?.ForgetSidePanelState(sessionId);

                // The host has confirmed the chat is gone, so drop its row now; the
                // reload below only reconciles the rest of the list.
                RemoveSessionRow(sessionId);

                await LoadSessionsAsync();

                // If the deleted session was currently active, switch to another session or create a new one
                if (wasActive)
                {
                    if (Sessions.Count > 0)
                    {
                        var first = Sessions[0];
                        SidebarSessionList.SelectedItem = first;
                        if (_chatView != null)
                        {
                            await _chatView.OpenSessionAsync(first.Id, first.Title);
                        }
                    }
                    else
                    {
                        OnNewChatClick(this, new RoutedEventArgs());
                    }
                }
                else if (Sessions.Count == 0)
                {
                    OnNewChatClick(this, new RoutedEventArgs());
                }

                // A partial result still deleted the chat: the session file is gone and
                // only its artifacts are left. The old boolean read the "error" envelope
                // as a failed delete and told the user the chat was still there.
                if (outcome.Partial)
                {
                    await OfferSessionArtifactRetryAsync(sessionId, outcome.Message);
                }
            }
        }

        private void RemoveSessionRow(string sessionId)
        {
            for (int i = Sessions.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Sessions[i].Id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    Sessions.RemoveAt(i);
                    RenderWorkspaceSidebar();
                    RefreshSessionTitleWaves();
                    return;
                }
            }
        }

        /// <summary>
        /// The chat is already gone; the host could not remove its artifacts. Says so
        /// and offers the retry the host itself asked for, without ever calling the
        /// chat undeleted.
        /// </summary>
        /// <remarks>
        /// Retrying only ever re-issues the same DELETE. Once the session file is gone
        /// the host can no longer report "partial" — it answers "Session không tồn tại"
        /// whether the artifacts were reclaimed or not — so a retry that is not partial
        /// ends as unconfirmed cleanup rather than as success or as another failed
        /// delete. The leftovers are reclaimed on expiry regardless.
        /// </remarks>
        private async Task OfferSessionArtifactRetryAsync(string sessionId, string? message)
        {
            if (_apiService is null)
            {
                return;
            }

            while (ShellRoot.XamlRoot != null)
            {
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
                    XamlRoot = ShellRoot.XamlRoot
                };

                if (await ContentDialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
                {
                    return;
                }

                var retry = await _apiService.DeleteSessionAsync(sessionId);
                if (retry.Deleted)
                {
                    return;
                }

                if (!retry.Partial)
                {
                    await ShowNoticeAsync(
                        "Cleanup not confirmed",
                        "The chat was deleted. The cleanup ran again, but the host could not confirm the artifacts were removed."
                            + "\n\nLeftover artifacts are reclaimed automatically when they expire.");
                    return;
                }

                message = retry.Message;
            }
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
                _chatView?.ProviderCatalogState);
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
