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
        private string _workspaceSearchText = string.Empty;
        private bool _workspaceFilterActive;
        private string? _activeSessionId;
        private string? _selectedWorkspaceKey;
        private bool _suppressSidebarSelection;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _workspaceSearchTimer;
        private const int WorkspaceGroupPageSize = 5;
        private readonly ObservableCollection<object> _sidebarRows = new();
        private readonly List<WorkspaceGroup> _lastWorkspaceGroups = new();
        private readonly WorkspaceGroupPreferenceStore _workspaceGroups = new();
        private readonly WorkspacePinStore _workspacePins = new();
        private readonly SessionWorkspaceMapStore _sessionWorkspaces = new();
        private readonly HashSet<string> _groupShowAll = new(StringComparer.Ordinal);

        private static string PathStateKey(string canonicalPath) =>
            "path:" + canonicalPath.ToLowerInvariant();

        private void RenderWorkspaceSidebar()
        {
            var sessions = Sessions.ToList();
            var activeKey = ActiveWorkspaceKey();
            if (_workspaceFilterActive && activeKey is not null)
            {
                sessions = sessions
                    .Where(session => WorkspaceSessionProjection.KeyFor(session) == activeKey)
                    .ToList();
            }

            var projection = WorkspaceSessionProjection.Project(sessions, _workspaceSearchText);
            var searching = !string.IsNullOrWhiteSpace(_workspaceSearchText);
            var groups = WorkspaceSessionProjection.MergePinned(
                projection.Groups,
                _workspacePins.PinnedPaths);
            if (searching)
            {
                groups = groups.Where(group => group.Sessions.Count > 0).ToList();
            }

            _suppressSidebarSelection = true;
            try
            {
                _sidebarRows.Clear();
                _lastWorkspaceGroups.Clear();
                _lastWorkspaceGroups.AddRange(groups);
                var first = true;
                foreach (var group in groups)
                {
                    var expanded = searching
                        || _workspaceGroups.IsExpanded(
                            group.Key,
                            defaultExpanded: activeKey is null ? first : group.Key == activeKey)
                        || (!string.IsNullOrEmpty(group.Path)
                            && _workspaceGroups.IsExpanded(PathStateKey(group.Path), defaultExpanded: false));
                    first = false;
                    _sidebarRows.Add(new WorkspaceGroupRow(group, expanded));
                    if (!expanded)
                    {
                        continue;
                    }

                    if (group.Sessions.Count > WorkspaceGroupPageSize
                        && !_groupShowAll.Contains(group.Key))
                    {
                        foreach (var session in group.Sessions.Take(WorkspaceGroupPageSize))
                        {
                            _sidebarRows.Add(session);
                        }

                        _sidebarRows.Add(new WorkspaceShowMoreRow(
                            group.Key,
                            $"Show {group.Sessions.Count - WorkspaceGroupPageSize} more"));
                    }
                    else
                    {
                        foreach (var session in group.Sessions)
                        {
                            _sidebarRows.Add(session);
                        }
                    }
                }

                if (_activeSessionId is not null)
                {
                    SyncSidebarSelection(_activeSessionId);
                }
            }
            finally
            {
                _suppressSidebarSelection = false;
            }

            var matchCount = _sidebarRows.OfType<SessionInfo>().Count();
            WorkspaceSearchCount.Text = matchCount == 0
                ? "No matches"
                : matchCount == 1 ? "1 result" : $"{matchCount} results";
            WorkspaceSearchCount.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        }

        private string? ActiveWorkspaceKey()
        {
            if (_activeSessionId is null)
            {
                return null;
            }

            var active = Sessions.FirstOrDefault(session => session.Id == _activeSessionId);
            return active is null ? null : WorkspaceSessionProjection.KeyFor(active);
        }

        private void OnWorkspaceSearchClick(object sender, RoutedEventArgs e)
        {
            WorkspaceSearchBox.Visibility = Visibility.Visible;
            WorkspaceSearchBox.Focus(FocusState.Programmatic);
        }

        private void OnWorkspaceSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox box)
            {
                _workspaceSearchText = box.Text;
            }

            _workspaceSearchTimer ??= DispatcherQueue.CreateTimer();
            _workspaceSearchTimer.Interval = TimeSpan.FromMilliseconds(250);
            _workspaceSearchTimer.Tick -= OnWorkspaceSearchTick;
            _workspaceSearchTimer.Tick += OnWorkspaceSearchTick;
            _workspaceSearchTimer.Stop();
            _workspaceSearchTimer.Start();
        }

        private void OnWorkspaceSearchTick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
        {
            timer.Stop();
            RenderWorkspaceSidebar();
        }

        private void OnWorkspaceSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                ClearWorkspaceSearch();
                RenderWorkspaceSidebar();
            }
            else if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var first = _sidebarRows.OfType<SessionInfo>().FirstOrDefault();
                if (first is not null)
                {
                    SidebarSessionList.SelectedItem = first;
                }
            }
        }

        private void ClearWorkspaceSearch()
        {
            _workspaceSearchText = string.Empty;
            WorkspaceSearchBox.Text = string.Empty;
            WorkspaceSearchBox.Visibility = Visibility.Collapsed;
        }

        private void OnWorkspaceFilterClick(object sender, RoutedEventArgs e)
        {
            _workspaceFilterActive = !_workspaceFilterActive;
            WorkspaceFilterButton.Background = _workspaceFilterActive
                ? (Brush)Application.Current.Resources["RuAccentSoftBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            RenderWorkspaceSidebar();
        }

        private void OnWorkspaceGroupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                // Flip the row's effective state (not the stored default) and keep the
                // group-key and path-key flags in sync; otherwise a pinned path flag
                // left true by OnWorkspaceAddClick pins the group open forever.
                var expanded = !(button.CommandParameter as bool?
                    ?? _workspaceGroups.IsExpanded(key, defaultExpanded: true));
                _workspaceGroups.SetExpanded(key, expanded);
                var path = ResolveWorkspacePath(key);
                if (!string.IsNullOrEmpty(path))
                {
                    _workspaceGroups.SetExpanded(PathStateKey(path), expanded);
                }

                RenderWorkspaceSidebar();
            }
        }

        private void OnWorkspaceShowMoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                _groupShowAll.Add(key);
                RenderWorkspaceSidebar();
            }
        }

        /// <summary>Removes a pinned workspace; its folder and sessions stay untouched.</summary>
        private void OnWorkspaceRemoveClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string key) return;
            var path = ResolveWorkspacePath(key);
            if (path is null) return;

            _workspacePins.Unpin(path);
            if (_selectedWorkspaceKey == key)
            {
                _selectedWorkspaceKey = null;
            }

            RenderWorkspaceSidebar();
        }

        private async void OnWorkspaceGroupAddClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null || _chatView == null) return;
            if (sender is not Button button || button.Tag is not string key) return;
            var path = ResolveWorkspacePath(key);
            if (path is null) return;

            var session = await _apiService.CreateSessionAsync();
            if (session is null) return;
            _sessionWorkspaces.Set(session.Id, path);
            _chatView.SetRequestedWorkspace(path);
            await LoadSessionsAsync();

            _activeSessionId = session.Id;
            _selectedWorkspaceKey = key;
            _workspaceGroups.SetExpanded(key, true);
            RenderWorkspaceSidebar();

            OnNavClick(NavChat, new RoutedEventArgs());
            await _chatView.OpenSessionAsync(session.Id, session.Title);
        }

        private async void OnWorkspaceAddClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var canonical = WorkspacePinStore.Canonicalize(folder.Path);
            _workspacePins.Pin(canonical);
            _workspaceGroups.SetExpanded(PathStateKey(canonical), true);
            RenderWorkspaceSidebar();

            foreach (var group in _lastWorkspaceGroups)
            {
                if (!string.IsNullOrEmpty(group.Path)
                    && string.Equals(group.Path, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    _workspaceGroups.SetExpanded(group.Key, true);
                    RenderWorkspaceSidebar();
                    break;
                }
            }
        }

        private string? ResolveWorkspacePath(string key)
        {
            foreach (var group in _lastWorkspaceGroups)
            {
                if (group.Key == key)
                {
                    return group.Path;
                }

                if (!string.IsNullOrEmpty(group.Path) && PathStateKey(group.Path) == key)
                {
                    return group.Path;
                }
            }

            foreach (var pinned in _workspacePins.PinnedPaths)
            {
                if (PathStateKey(pinned) == key)
                {
                    return pinned;
                }
            }

            return null;
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
            if (e.Key == Windows.System.VirtualKey.Escape && _isSessionUsageOpen)
            {
                e.Handled = true;
                CloseSessionUsage();
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Escape && _isSettingsModalOpen)
            {
                e.Handled = true;
                CloseSettingsPopup();
                return;
            }

            var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (!ctrlDown) return;

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

        private void NudgeSidebarWidth(double delta)
        {
            if (_responsiveTier == ResponsiveLayoutTier.Narrow) return;

            double newWidth = SidebarColumn.ActualWidth + delta;
            if (newWidth <= 150)
            {
                SidebarColumn.MinWidth = 72;
                SidebarColumn.Width = new GridLength(72);
                ApplySidebarVisualMode(true);
            }
            else
            {
                SidebarColumn.MinWidth = 72;
                var clamped = Math.Clamp(newWidth, 180, 400);
                SidebarColumn.Width = new GridLength(clamped);
                _lastExpandedSidebarWidth = clamped;
                ApplySidebarVisualMode(false);
            }
        }
    }
}
