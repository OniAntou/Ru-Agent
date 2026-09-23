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
        private async void OnForkSessionMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is string sessionId && _apiService != null)
            {
                var targetSession = Sessions.FirstOrDefault(s => s.Id == sessionId);
                var branchTitle = targetSession != null ? $"{targetSession.Title} (Branch)" : "New Branch";
                var forked = await _apiService.ForkSessionAsync(sessionId, int.MaxValue, branchTitle);
                if (forked != null)
                {
                    await LoadSessionsAsync();
                    var newSession = Sessions.FirstOrDefault(s => s.Id == forked.Id);
                    if (newSession != null)
                    {
                        SidebarSessionList.SelectedItem = newSession;
                        if (_chatView != null)
                        {
                            await _chatView.OpenSessionAsync(newSession.Id, newSession.Title);
                        }
                    }
                }
            }
        }

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
            await dialog.ShowAsync();
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
            if (_apiService is null || ShellRoot.XamlRoot is null)
            {
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
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
                    $"Ru recognises {SessionFileLayout.ClaudeCodeId} transcripts, named after their session id, and {SessionFileLayout.CodexId} rollouts, named rollout-<when>-<thread>.");
                return;
            }

            var content = await Windows.Storage.FileIO.ReadTextAsync(file);
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
            await dialog.ShowAsync();
        }

        private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
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

                    var result = await dialog.ShowAsync();
                    if (result != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                bool deleted = await _apiService.DeleteSessionAsync(sessionId);
                if (!deleted)
                {
                    if (ShellRoot.XamlRoot != null)
                    {
                        var errorDialog = new ContentDialog
                        {
                            Title = "Could not delete chat",
                            Content = "Backend failed to delete this chat. Please try again later.",
                            CloseButtonText = "Close",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = ShellRoot.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                    }

                    return;
                }

                bool wasActive = string.Equals(_chatView?.CurrentSessionId, sessionId, StringComparison.OrdinalIgnoreCase);

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
            }
        }

        private void SessionItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SetSessionRowActionsVisible(sender, visible: true);
        }

        private void SessionItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SetSessionRowActionsVisible(sender, visible: false);
        }

        /// <summary>
        /// Reveals the row's actions in place of the clock at the right edge, and
        /// restores the clock on leave.
        /// </summary>
        /// <remarks>
        /// The clock is collapsed rather than dimmed: it is wider than the two 22px
        /// controls together, so leaving it visible would paint the time underneath
        /// them. Everything is right-aligned in one cell, so swapping them does not
        /// move anything else in the row.
        ///
        /// Hit testing is toggled with the opacity. Controls that stay invisible but
        /// still accept clicks would turn a stray click on the timestamp into a delete,
        /// or into a network read. Keyboard reach is deliberately not granted here —
        /// the row's context menu owns that path — so neither control is a tab stop.
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

            if (cell.FindName("SessionDeleteButton") is Button deleteButton)
            {
                deleteButton.Opacity = visible ? 1.0 : 0.0;
                deleteButton.IsHitTestVisible = visible;
            }

            if (cell.FindName("SessionUsageButton") is Button usageButton)
            {
                usageButton.Opacity = visible ? 1.0 : 0.0;
                usageButton.IsHitTestVisible = visible;
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
            if (sender is not Button button
                || button.Tag is not string sessionId
                || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            await OpenSessionUsageAsync(sessionId, (button.DataContext as SessionInfo)?.Title);
        }

        private bool _isSessionUsageOpen;

        private async Task OpenSessionUsageAsync(string sessionId, string? sessionTitle)
        {
            if (_apiService is null)
            {
                return;
            }

            _isSessionUsageOpen = true;
            SessionUsageOverlay.Visibility = Visibility.Visible;
            ApplySessionUsageCardSize();
            CloseSessionUsageButton?.Focus(FocusState.Programmatic);

            await SessionUsagePanelControl.LoadAsync(_apiService, sessionId, sessionTitle);
        }

        private void CloseSessionUsage()
        {
            if (!_isSessionUsageOpen)
            {
                return;
            }

            _isSessionUsageOpen = false;
            SessionUsageOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnSessionUsageOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                ApplySessionUsageCardSize();
            }
        }

        /// <summary>
        /// A fixed card keeps the panel from jittering as rows arrive; it shrinks with
        /// the window instead of overflowing it, with a floor below which the rows
        /// would have no room to render.
        /// </summary>
        private void ApplySessionUsageCardSize()
        {
            if (SessionUsageOverlay.ActualWidth <= 0 || SessionUsageOverlay.ActualHeight <= 0)
            {
                return;
            }

            SessionUsageCard.Width = Math.Min(560, Math.Max(360, SessionUsageOverlay.ActualWidth - 48));
            SessionUsageCard.Height = Math.Min(560, Math.Max(320, SessionUsageOverlay.ActualHeight - 40));
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
