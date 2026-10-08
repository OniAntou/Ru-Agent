using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The right side surface's hosts: the floating summary card and the
    /// full-height details panel. It renders what it is told and raises what the
    /// user did; which host shows and which tabs are open is decided by
    /// <see cref="AgentSidePanelPolicy"/> in the view.
    /// </summary>
    public sealed partial class AgentSidePanelControl : UserControl
    {
        public ObservableCollection<AgentTaskRow> Tasks { get; } = new();

        public ObservableCollection<ChangesTabRow> Changes { get; } = new();

        public event EventHandler? ExpandRequested;

        public event EventHandler? CollapseRequested;

        public event EventHandler? FullscreenToggleRequested;

        public event EventHandler<SidePanelTabKind>? TabActivated;

        public event EventHandler<SidePanelTabKind>? TabClosed;

        public event EventHandler<SidePanelTabKind>? TabAddRequested;

        public event EventHandler<SidePanelTabMove>? TabMoved;

        public event EventHandler<string>? TaskStopRequested;

        public event EventHandler<double>? WidthChanged;

        public event EventHandler<double>? WidthCommitted;

        private AgentSidePanelMode _mode = AgentSidePanelMode.Hidden;
        private IReadOnlyList<SidePanelTabKind> _openTabs = Array.Empty<SidePanelTabKind>();
        private SidePanelTabKind _activeTab = SidePanelTabKind.Changes;
        private bool _changesChipHovered;
        private bool _tasksChipHovered;
        private bool _filesChipHovered;
        private SidePanelTabKind? _draggingTab;
        private Border? _draggingChip;
        private double _dragStartX;
        private bool _isDragActive;
        private bool _isResizing;
        private double _resizeStartWidth;
        private double _resizeStartX;

        public AgentSidePanelControl()
        {
            InitializeComponent();
        }

        public void SetSessionTitle(string title) => CardTitleText.Text = title;

        public void SetTasksStatus(string status)
        {
            CardTasksStatusText.Text = status;
            PanelTasksStatusText.Text = status;
        }

        public void RenderChanges(IReadOnlyList<ChangesTabRow> rows, int added, int removed)
        {
            Changes.Clear();
            foreach (var row in rows)
            {
                Changes.Add(row);
            }

            CardAddedText.Text = "+" + added;
            CardRemovedText.Text = "−" + removed;
            PanelAddedText.Text = "+" + added;
            PanelRemovedText.Text = "−" + removed;
            ChangesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChangesList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void RenderTasks(IReadOnlyList<AgentTaskRow> rows, string status)
        {
            Tasks.Clear();
            foreach (var row in rows)
            {
                Tasks.Add(row);
            }

            SetTasksStatus(status);
        }

        public void ApplyState(
            AgentSidePanelMode mode,
            IReadOnlyList<SidePanelTabKind> openTabs,
            SidePanelTabKind activeTab,
            bool fullscreen,
            double panelWidth)
        {
            var previous = _mode;
            var previousActive = _activeTab;
            _mode = mode;
            _openTabs = openTabs;
            _activeTab = activeTab;

            bool showCard = mode == AgentSidePanelMode.Card;
            bool showPanel = mode == AgentSidePanelMode.Panel;
            CardHost.Visibility = showCard ? Visibility.Visible : Visibility.Collapsed;
            PanelHost.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;

            RefreshChip(ChangesChip, ChangesTabLabel, SidePanelTabKind.Changes);
            RefreshChip(TasksChip, TasksTabLabel, SidePanelTabKind.Tasks);
            RefreshChip(FilesChip, FilesTabLabel, SidePanelTabKind.Files);
            AddTabButton.Visibility = openTabs.Count < 3 ? Visibility.Visible : Visibility.Collapsed;
            SyncChipOrder(openTabs);

            ChangesBody.Visibility = showPanel && activeTab == SidePanelTabKind.Changes ? Visibility.Visible : Visibility.Collapsed;
            TasksBody.Visibility = showPanel && activeTab == SidePanelTabKind.Tasks ? Visibility.Visible : Visibility.Collapsed;
            FilesBody.Visibility = showPanel && activeTab == SidePanelTabKind.Files ? Visibility.Visible : Visibility.Collapsed;

            if (showPanel && activeTab != previousActive)
            {
                PlayBodyTransition(activeTab switch
                {
                    SidePanelTabKind.Changes => (FrameworkElement)ChangesBody,
                    SidePanelTabKind.Tasks => TasksBody,
                    _ => FilesBody,
                });
            }

            bool push = showPanel && !fullscreen;
            PanelHost.HorizontalAlignment = push ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            if (push)
            {
                PanelHost.Width = AgentSidePanelPolicy.ClampWidth(panelWidth);
            }
            else
            {
                PanelHost.ClearValue(FrameworkElement.WidthProperty);
            }

            PanelResizeGrip.Visibility = push ? Visibility.Visible : Visibility.Collapsed;
            PanelDivider.Visibility = push ? Visibility.Visible : Visibility.Collapsed;

            FullscreenIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
            string fullscreenName = fullscreen ? "Exit fullscreen" : "Fullscreen panel";
            ToolTipService.SetToolTip(FullscreenButton, fullscreenName);
            AutomationProperties.SetName(FullscreenButton, fullscreenName);

            if (mode != previous && mode != AgentSidePanelMode.Hidden)
            {
                PlayEntrance(showPanel ? PanelHost : CardHost, showPanel ? PanelSlide : CardSlide);
            }
        }

        private void RefreshChip(Border chip, TextBlock labelText, SidePanelTabKind kind)
        {
            bool isOpen = _openTabs.Contains(kind);
            bool isActive = _activeTab == kind;
            bool isHovered = IsChipHovered(kind);

            chip.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            bool raised = isActive || isHovered;
            chip.Background = raised
                ? (Brush)Application.Current.Resources["RuSurfaceBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            chip.BorderBrush = isActive
                ? (Brush)Application.Current.Resources["RuLineBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            labelText.Foreground = (Brush)Application.Current.Resources[raised ? "RuInkBrush" : "RuMutedBrush"];
            labelText.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private bool IsChipHovered(SidePanelTabKind kind) =>
            kind switch
            {
                SidePanelTabKind.Changes => _changesChipHovered,
                SidePanelTabKind.Tasks => _tasksChipHovered,
                _ => _filesChipHovered,
            };

        private void OnChipPointerEntered(object sender, PointerRoutedEventArgs e) =>
            SetChipHovered(sender, true);

        private void OnChipPointerExited(object sender, PointerRoutedEventArgs e) =>
            SetChipHovered(sender, false);

        private void SetChipHovered(object sender, bool hovered)
        {
            if (sender is not FrameworkElement { Tag: string kind })
            {
                return;
            }

            switch (kind)
            {
                case "Changes":
                    _changesChipHovered = hovered;
                    break;
                case "Tasks":
                    _tasksChipHovered = hovered;
                    break;
                default:
                    _filesChipHovered = hovered;
                    break;
            }

            RefreshChip(ChangesChip, ChangesTabLabel, SidePanelTabKind.Changes);
            RefreshChip(TasksChip, TasksTabLabel, SidePanelTabKind.Tasks);
            RefreshChip(FilesChip, FilesTabLabel, SidePanelTabKind.Files);
        }

        private void SyncChipOrder(IReadOnlyList<SidePanelTabKind> openTabs)
        {
            int target = 0;
            foreach (var kind in openTabs)
            {
                var chip = ChipFor(kind);
                int current = ChipStrip.Children.IndexOf(chip);
                if (current < 0)
                {
                    continue;
                }

                if (current != target)
                {
                    ChipStrip.Children.Move((uint)current, (uint)target);
                }

                target++;
            }
        }

        private Border ChipFor(SidePanelTabKind kind) =>
            kind switch
            {
                SidePanelTabKind.Changes => ChangesChip,
                SidePanelTabKind.Tasks => TasksChip,
                _ => FilesChip,
            };

        private static SidePanelTabKind KindFromTag(string tag) =>
            tag switch
            {
                "Tasks" => SidePanelTabKind.Tasks,
                "Files" => SidePanelTabKind.Files,
                _ => SidePanelTabKind.Changes,
            };

        private int IndexOfOpen(SidePanelTabKind kind)
        {
            for (var index = 0; index < _openTabs.Count; index++)
            {
                if (_openTabs[index] == kind)
                {
                    return index;
                }
            }

            return -1;
        }

        private void OnChipPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border { Tag: string tag } chip)
            {
                return;
            }

            _draggingTab = KindFromTag(tag);
            _draggingChip = chip;
            _dragStartX = e.GetCurrentPoint(ChipStrip).Position.X;
            _isDragActive = false;
        }

        private void OnChipPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_draggingTab is not { } kind || _draggingChip is null)
            {
                return;
            }

            double x = e.GetCurrentPoint(ChipStrip).Position.X;
            if (!_isDragActive)
            {
                if (Math.Abs(x - _dragStartX) < 8)
                {
                    return;
                }

                _isDragActive = true;
                _draggingChip.CapturePointer(e.Pointer);
                _draggingChip.Opacity = 0.85;
            }

            int current = IndexOfOpen(kind);
            if (current < 0)
            {
                return;
            }

            int insertion = 0;
            foreach (var child in ChipStrip.Children)
            {
                if (child is not FrameworkElement chip || chip.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var bounds = chip.TransformToVisual(ChipStrip).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, chip.ActualWidth, chip.ActualHeight));
                if (x > bounds.X + (bounds.Width / 2))
                {
                    insertion++;
                }
            }

            int targetIndex = insertion > current ? insertion - 1 : insertion;
            if (targetIndex != current)
            {
                TabMoved?.Invoke(this, new SidePanelTabMove(kind, targetIndex));
            }
        }

        private void OnChipPointerReleased(object sender, PointerRoutedEventArgs e) =>
            EndChipDrag(e);

        private void OnChipPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_draggingChip is { } chip)
            {
                chip.Opacity = 1;
            }

            _isDragActive = false;
            _draggingTab = null;
            _draggingChip = null;
        }

        private void EndChipDrag(PointerRoutedEventArgs e)
        {
            if (_isDragActive && _draggingChip is { } chip)
            {
                chip.ReleasePointerCapture(e.Pointer);
                chip.Opacity = 1;
            }

            _isDragActive = false;
            _draggingTab = null;
            _draggingChip = null;
        }

        private static void PlayBodyTransition(FrameworkElement body)
        {
            if (!UiTiming.AnimationsEnabled)
            {
                body.Opacity = 1;
                return;
            }

            body.Opacity = 0;
            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, body);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
        }

        private static void PlayEntrance(FrameworkElement host, TranslateTransform slide)
        {
            if (!UiTiming.AnimationsEnabled)
            {
                host.Opacity = 1;
                slide.X = 0;
                return;
            }

            host.Opacity = 0;
            slide.X = 16;

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, host);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var glide = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(glide, slide);
            Storyboard.SetTargetProperty(glide, "X");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(glide);
            storyboard.Begin();
        }

        private void OnExpandClick(object sender, RoutedEventArgs e) =>
            ExpandRequested?.Invoke(this, EventArgs.Empty);

        private void OnCollapseClick(object sender, RoutedEventArgs e) =>
            CollapseRequested?.Invoke(this, EventArgs.Empty);

        private void OnFullscreenClick(object sender, RoutedEventArgs e) =>
            FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

        private void OnChangesTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Changes);

        private void OnTasksTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnChangesTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Changes);

        private void OnTasksTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnFilesTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Files);

        private void OnFilesTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Files);

        private void OnAddTabClick(object sender, RoutedEventArgs e)
        {
            var closed = new List<SidePanelTabKind>();
            foreach (var kind in new[] { SidePanelTabKind.Changes, SidePanelTabKind.Tasks, SidePanelTabKind.Files })
            {
                if (!_openTabs.Contains(kind))
                {
                    closed.Add(kind);
                }
            }

            if (closed.Count == 0)
            {
                return;
            }

            var items = new StackPanel { Spacing = 2 };
            var flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.Bottom,
                FlyoutPresenterStyle = (Style)Resources["SidePanelFlyoutPresenterStyle"],
            };
            foreach (var kind in closed)
            {
                var item = BuildAddTabItem(kind);
                item.Click += (_, _) =>
                {
                    flyout.Hide();
                    TabAddRequested?.Invoke(this, kind);
                };
                items.Children.Add(item);
            }

            flyout.Content = items;
            flyout.ShowAt(AddTabButton);
        }

        private Button BuildAddTabItem(SidePanelTabKind kind)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(new FontIcon
            {
                Glyph = kind == SidePanelTabKind.Changes ? "\uE70F" : "\uE756",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
            });
            content.Children.Add(new TextBlock
            {
                Text = kind == SidePanelTabKind.Changes ? "Changes" : "Tasks",
                FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["RuInkBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });

            return new Button
            {
                Content = content,
                Style = (Style)Resources["SidePanelFlyoutItemStyle"],
            };
        }

        private void OnChangeRowToggleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ChangesTabRow row })
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        private void OnTaskStopClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string executionId })
            {
                TaskStopRequested?.Invoke(this, executionId);
            }
        }

        private void OnGripPointerEntered(object sender, PointerRoutedEventArgs e) =>
            PanelDivider.Opacity = 0.9;

        private void OnGripPointerExited(object sender, PointerRoutedEventArgs e) =>
            PanelDivider.Opacity = 0.6;

        private void OnGripPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (PanelHost.Visibility != Visibility.Visible
                || PanelHost.HorizontalAlignment != HorizontalAlignment.Right)
            {
                return;
            }

            _isResizing = true;
            _resizeStartWidth = PanelHost.Width;
            _resizeStartX = e.GetCurrentPoint(this).Position.X;
            PanelResizeGrip.CapturePointer(e.Pointer);
        }

        private void OnGripPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing)
            {
                return;
            }

            double currentX = e.GetCurrentPoint(this).Position.X;
            double width = AgentSidePanelPolicy.ClampWidth(_resizeStartWidth + (_resizeStartX - currentX));
            PanelHost.Width = width;
            WidthChanged?.Invoke(this, width);
        }

        private void OnGripPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing)
            {
                return;
            }

            _isResizing = false;
            PanelResizeGrip.ReleasePointerCapture(e.Pointer);
            WidthCommitted?.Invoke(this, PanelHost.Width);
        }
    }
}
