using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private void InitializeTabInteractions()
    {
        // Button consumes pointer presses for its click gesture. Observe handled
        // events as well so a label can become a drag after the movement threshold.
        foreach (var chip in new[] { ChangesChip, TasksChip, SubagentsChip, FilesChip, PreviewChip, TerminalChip })
        {
            chip.AddHandler(PointerPressedEvent, new PointerEventHandler(OnChipPointerPressed), handledEventsToo: true);
            chip.AddHandler(PointerMovedEvent, new PointerEventHandler(OnChipPointerMoved), handledEventsToo: true);
            chip.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnChipPointerReleased), handledEventsToo: true);
        }
    }

    private bool _tabRevealQueued;

    private void OnTabStripSizeChanged(object sender, SizeChangedEventArgs e) => QueueActiveTabReveal();

    private void QueueActiveTabReveal()
    {
        if (_tabRevealQueued) return;
        _tabRevealQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _tabRevealQueued = false;
            if (_mode != AgentSidePanelMode.Panel) return;
            // A resize can clip the selected tab without changing selection.
            // Leave room for its icon, padding and close action before scrolling.
            var chip = ChipFor(_activeTab);
            if (chip.Child is StackPanel children && children.Children[0] is Button { Content: StackPanel content }
                && content.Children.LastOrDefault() is TextBlock label)
                label.MaxWidth = Math.Clamp(TabStripScroll.ViewportWidth - 80, 32, 112);
            TabStripScroll.UpdateLayout();
            var bounds = chip.TransformToVisual(ChipStrip).TransformBounds(
                new Windows.Foundation.Rect(0, 0, chip.ActualWidth, chip.ActualHeight));
            double offset = TabStripScroll.HorizontalOffset;
            if (bounds.Left < offset) offset = bounds.Left;
            else if (bounds.Right > offset + TabStripScroll.ViewportWidth)
                offset = bounds.Right - TabStripScroll.ViewportWidth;
            TabStripScroll.ChangeView(Math.Max(0, offset), null, null, disableAnimation: true);
        });
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
        // Inactive tabs keep their recognizable icon. The active label remains
        // readable even with all six views open at the minimum panel width.
        labelText.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        labelText.MaxWidth = Math.Clamp(TabStripScroll.ViewportWidth - 80, 32, 112);
        labelText.TextTrimming = TextTrimming.CharacterEllipsis;
        var close = kind switch
        {
            SidePanelTabKind.Changes => ChangesTabCloseButton,
            SidePanelTabKind.Tasks => TasksTabCloseButton,
            SidePanelTabKind.Subagents => SubagentsTabCloseButton,
            SidePanelTabKind.Files => FilesTabCloseButton,
            SidePanelTabKind.Preview => PreviewTabCloseButton,
            _ => TerminalTabCloseButton,
        };
        close.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        var tab = kind switch
        {
            SidePanelTabKind.Changes => ChangesTabButton,
            SidePanelTabKind.Tasks => TasksTabButton,
            SidePanelTabKind.Subagents => SubagentsTabButton,
            SidePanelTabKind.Files => FilesTabButton,
            SidePanelTabKind.Preview => PreviewTabButton,
            _ => TerminalTabButton,
        };
        AutomationProperties.SetHelpText(tab, isActive ? "Selected tab" : "Open tab");
    }

    private bool IsChipHovered(SidePanelTabKind kind) =>
        kind switch
        {
            SidePanelTabKind.Changes => _changesChipHovered,
            SidePanelTabKind.Tasks => _tasksChipHovered,
            SidePanelTabKind.Subagents => _subagentsChipHovered,
            SidePanelTabKind.Files => _filesChipHovered,
            SidePanelTabKind.Preview => _previewChipHovered,
            _ => _terminalChipHovered,
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
            case "Subagents":
                _subagentsChipHovered = hovered;
                break;
            case "Tasks":
                _tasksChipHovered = hovered;
                break;
            case "Files":
                _filesChipHovered = hovered;
                break;
            case "Preview":
                _previewChipHovered = hovered;
                break;
            default:
                _terminalChipHovered = hovered;
                break;
        }

        RefreshChip(ChangesChip, ChangesTabLabel, SidePanelTabKind.Changes);
        RefreshChip(TasksChip, TasksTabLabel, SidePanelTabKind.Tasks);
        RefreshChip(SubagentsChip, SubagentsTabLabel, SidePanelTabKind.Subagents);
        RefreshChip(FilesChip, FilesTabLabel, SidePanelTabKind.Files);
        RefreshChip(TerminalChip, TerminalTabLabel, SidePanelTabKind.Terminal);
        RefreshChip(PreviewChip, PreviewTabLabel, SidePanelTabKind.Preview);
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
            SidePanelTabKind.Subagents => SubagentsChip,
            SidePanelTabKind.Files => FilesChip,
            SidePanelTabKind.Preview => PreviewChip,
            _ => TerminalChip,
        };

    private static SidePanelTabKind KindFromTag(string tag) =>
        tag switch
        {
            "Tasks" => SidePanelTabKind.Tasks,
            "Subagents" => SidePanelTabKind.Subagents,
            "Files" => SidePanelTabKind.Files,
            "Terminal" => SidePanelTabKind.Terminal,
            "Preview" => SidePanelTabKind.Preview,
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
        if (sender is not Border { Tag: string tag } chip
            || !e.GetCurrentPoint(ChipStrip).Properties.IsLeftButtonPressed)
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
        if (!e.GetCurrentPoint(ChipStrip).Properties.IsLeftButtonPressed)
        {
            EndChipDrag(e);
            return;
        }

        double x = e.GetCurrentPoint(ChipStrip).Position.X;
        if (!_isDragActive)
        {
            if (Math.Abs(x - _dragStartX) < 8)
            {
                return;
            }

            // The pressed label button owns capture. Release that click gesture
            // before the chip takes over; CapturePointer otherwise returns false.
            var draggingChip = _draggingChip;
            if (draggingChip.Child is StackPanel buttons)
                foreach (var button in buttons.Children.OfType<Button>()) button.ReleasePointerCapture(e.Pointer);
            bool captured = draggingChip.CapturePointer(e.Pointer);
            if (!captured) return;
            _isDragActive = true;
            draggingChip.Opacity = 0.85;
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
        // A child button losing its own capture bubbles through this Border;
        // only losing the chip's capture ends a tab drag.
        if (!ReferenceEquals(e.OriginalSource, _draggingChip)) return;
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

}
