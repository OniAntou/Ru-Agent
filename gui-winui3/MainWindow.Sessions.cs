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
using RuApp.ViewModels;
using RuApp.Views;

namespace RuApp
{
    public sealed partial class MainWindow
    {
        private bool _suppressSidebarSelection;

        /// <summary>
        /// The view model rebuilt the rows: put the native list selection back on the
        /// open session without letting that programmatic change open it again.
        /// </summary>
        private void OnSidebarRendered()
        {
            _suppressSidebarSelection = true;
            try
            {
                if (Sidebar.ActiveSessionId is { } active)
                {
                    SyncSidebarSelection(active);
                    // The open chat keeps the title it was opened with; a session titled by
                    // its first prompt afterwards must reach the side card too.
                    if (Sidebar.Sessions.FirstOrDefault(session => session.Id == active) is { } open)
                    {
                        _chatView?.UpdateSessionTitle(open.Id, open.Title);
                    }
                }
            }
            finally
            {
                _suppressSidebarSelection = false;
            }

            var truncation = Sidebar.IsTruncated ? Visibility.Visible : Visibility.Collapsed;
            if (SidebarSessionTruncationWarning.Visibility != truncation)
            {
                SidebarSessionTruncationWarning.Visibility = truncation;
            }

            _sessionWaveAnimationsEnabled = UiTiming.AnimationsEnabled;
            RefreshSessionTitleWaves();
        }

        private void OnWorkspaceGroupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string key } button)
            {
                Sidebar.ToggleGroup(key, button.CommandParameter as bool?);
            }
        }

        private void OnWorkspaceShowMoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string key })
            {
                Sidebar.ShowAll(key);
            }
        }

        /// <summary>Removes a pinned workspace; its folder and sessions stay untouched.</summary>
        private void OnWorkspaceRemoveClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string key })
            {
                Sidebar.Unpin(key);
            }
        }

        private async void OnWorkspaceGroupAddClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Button { Tag: string key }) return;
                var path = Sidebar.ResolveWorkspacePath(key);
                if (path is null && key != SidebarSessionOrganization.RecentsKey) return;
                await CreateDraftSessionAsync(key, path);
            }
            catch (Exception ex)
            {
                // async void: a failed create (or a dialog collision) must leave a
                // bounded status instead of an unhandled exception.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Workspace add failed: {ex.Message}");
                SetSessionActionError("Could not add a chat to this workspace");
            }
        }

        private WorkspaceFolderPicker? _workspaceFolderPicker;

        private async void OnWorkspaceAddClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
                _workspaceFolderPicker ??= WorkspaceFolderPicker.For(windowId);
                var folder = await _workspaceFolderPicker.PickAsync();
                if (folder is not null)
                {
                    Sidebar.Pin(folder);
                }
            }
            catch (Exception ex)
            {
                // async void: the picker can throw (interop failure) and the
                // sidebar update must not die silently.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Workspace picker failed: {ex.GetType().Name} (0x{ex.HResult:X8})");
                SetSessionActionError("Could not add the workspace");
            }
        }

        private void WorkspaceGroupToggle_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Hover fill comes from the ListViewItem container alone; the
                // row button paints nothing so there is never a double border.
                SetWorkspaceGroupChromeVisible(button, visible: true);
            }
        }

        private void WorkspaceGroupToggle_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                SetWorkspaceGroupChromeVisible(button, visible: false);
            }
        }

        private static void SetWorkspaceGroupChromeVisible(Button button, bool visible)
        {
            if (button.FindName("WorkspaceGroupAddButton") is Button addButton)
            {
                addButton.Opacity = visible ? 1.0 : 0.55;
            }

            if (button.FindName("WorkspaceGroupChevron") is UIElement chevron)
            {
                chevron.Opacity = visible ? 1.0 : 0.55;
            }
        }

        private void OnShellPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && Palette.IsOpen)
            {
                e.Handled = true;
                CloseCommandPalette();
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Escape && _isSessionUsageOpen)
            {
                e.Handled = true;
                CloseSessionUsage();
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Escape && Shell.IsSettingsOpen)
            {
                // Nested flyouts own the first Escape; closing the owner here
                // leaves the popup floating above the chat.
                if (_settingsView?.HasOpenComboBox == true || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(SettingsModalOverlay.XamlRoot)
                    .Any(popup => popup.Child is FlyoutPresenter or MenuFlyoutPresenter)) return;
                e.Handled = true;
                CloseSettingsPopup();
                return;
            }

            if (Shell.IsSettingsOpen) return;

            var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (!ctrlDown) return;

            if (TryHandleShellShortcut(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Windows.System.VirtualKey.N)
            {
                e.Handled = true;
                OnNewChatClick(NavNewChat, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Number1)
            {
                e.Handled = true;
                OnNavClick(NavChat, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Number2)
            {
                e.Handled = true;
                OnNavClick(NavSettings, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Left)
            {
                e.Handled = true;
                NudgeSidebarWidth(-40);
            }
            else if (e.Key == Windows.System.VirtualKey.Right)
            {
                e.Handled = true;
                NudgeSidebarWidth(40);
            }
        }


    }
}
