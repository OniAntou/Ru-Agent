using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private const double TodoSegmentWidth = 8;
    private const double TodoSegmentHeight = 3;
    private const double TodoBarWidth = 74;

    // The drawer estimate matches the row minimum and the scroll viewer's vertical
    // padding. A wrapped row can still scroll; the target is a bound, not a measurement.
    private const double TodoRowHeightEstimate = 28;
    private const double TodoRowGapEstimate = 2;
    private const double TodoListPaddingEstimate = 16;

    private readonly List<Border> _todoSegmentVisuals = new();
    private Border? _todoActiveSegment;
    private Storyboard? _todoSegmentPulse;
    private Storyboard? _todoBarStoryboard;
    private Storyboard? _todoListStoryboard;
    private bool _todoListOpen;
    private double _todoListTargetHeight;

    // Driven by chronological activity data, never by recycled visual rows.
    private void UpdateTodoPanel(IReadOnlyList<TodoItemModel>? todos)
    {
        _latestTodos = todos;
        var state = AgentTodoPanelPresentation.Create(todos, RootGrid.ActualHeight, _todoPanelExpanded);
        if (!state.IsVisible)
        {
            ClearTodoPanel();
            return;
        }

        bool listOpen = _todoPanelExpanded;
        _todoListTargetHeight = EstimateTodoListHeight(todos?.Count ?? 0, state.ListHeight);

        UpdateTodoHeader(state, listOpen);
        UpdateTodoCount(state);
        ApplyTodoMark(state);
        ApplyTodoProgress(state);
        TodoPanelScroll.MaxHeight = state.ListHeight;

        // The list opens above the line, so the chevron points the way it will open.
        TodoPanelToggleIcon.Glyph = listOpen ? "\uE70D" : "\uE70E";
        TodoPanelToggleIcon.Visibility = Visibility.Visible;
        string toggleName = listOpen ? "Collapse task list" : "Expand task list";
        AutomationProperties.SetName(TodoPanelToggle, toggleName);
        ToolTipService.SetToolTip(TodoPanelToggle, toggleName);
        if (listOpen)
        {
            TodoPanelList.Render(todos);
        }

        ApplyTodoListState(listOpen);

        // An approval or question card floats over the composer and the shelf; the shelf
        // yields while one is up instead of showing its header above the card's edge.
        UiMotion.SetVisible(TodoPanel, !IsPromptCardShown);
    }

    private void UpdateTodoHeader(AgentTodoPanelPresentation state, bool listOpen)
    {
        string primary = state.HeaderPrimary;
        if (!string.Equals(TodoPanelHeader.Text, primary, StringComparison.Ordinal))
        {
            TodoPanelHeader.Text = primary;
            // A task advance or the completion sentence lands rather than snapping.
            TodoPanelHeader.Opacity = 0.35;
            UiMotion.FadeOpacity(TodoPanelHeader, 1);
        }

        // The collapsed shelf is one line; the next task stays available in the header
        // tooltip so folding the list does not hide it entirely.
        string headerTooltip = string.IsNullOrEmpty(state.HeaderSecondary)
            ? primary
            : $"{primary} — {state.HeaderSecondary}";
        ToolTipService.SetToolTip(TodoPanelHeader, headerTooltip);

        bool showNext = listOpen && !string.IsNullOrEmpty(state.HeaderSecondary);
        TodoPanelNext.Text = state.HeaderSecondary;
        if (showNext && TodoPanelNext.Visibility != Visibility.Visible)
        {
            TodoPanelNext.Visibility = Visibility.Visible;
            TodoPanelNext.Opacity = 0;
            UiMotion.FadeOpacity(TodoPanelNext, 1);
        }
        else if (!showNext)
        {
            TodoPanelNext.Visibility = Visibility.Collapsed;
            TodoPanelNext.Opacity = 1;
        }

        ToolTipService.SetToolTip(TodoPanelNext, state.HeaderSecondary);
    }

    private void UpdateTodoCount(AgentTodoPanelPresentation state)
    {
        string text = $"{state.Done}/{state.Total}";
        if (string.Equals(TodoPanelCount.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        TodoPanelCount.Text = text;
        TodoPanelCount.Opacity = 0.45;
        UiMotion.FadeOpacity(TodoPanelCount, 1);
    }

    private void ApplyTodoMark(AgentTodoPanelPresentation state)
    {
        // The two marks cross-fade in place; they both stay in the 16px slot so a
        // state change never moves the line beside them.
        SetMarkOpacity(TodoPanelMarkDot, state.HasActive ? 1 : 0);
        SetMarkOpacity(TodoPanelMarkRing, state.HasActive ? 0 : 1);
    }

    private static void SetMarkOpacity(FrameworkElement element, double target)
    {
        if (Math.Abs(element.Opacity - target) < 0.01)
        {
            return;
        }

        UiMotion.FadeOpacity(element, target);
    }

    private void ApplyTodoProgress(AgentTodoPanelPresentation state)
    {
        AutomationProperties.SetName(
            state.UseSegments ? TodoPanelSegments : TodoPanelBar,
            $"Tasks {state.Done} of {state.Total} done");

        if (state.UseSegments)
        {
            EnsureTodoSegments(state.Segments.Count);
            for (int index = 0; index < state.Segments.Count; index++)
            {
                // FadeBrush animates a private solid colour, so the shared theme brushes
                // never carry the transition.
                UiMotion.FadeBrush(
                    _todoSegmentVisuals[index],
                    Border.BackgroundProperty,
                    TodoSegmentBrush(state.Segments[index]));
            }

            int activeIndex = -1;
            for (int index = 0; index < state.Segments.Count; index++)
            {
                if (state.Segments[index] == TodoSegmentState.Active)
                {
                    activeIndex = index;
                    break;
                }
            }

            StartTodoSegmentPulse(activeIndex >= 0 ? _todoSegmentVisuals[activeIndex] : null);
            TodoPanelSegments.Visibility = Visibility.Visible;
            TodoPanelBar.Visibility = Visibility.Collapsed;
            return;
        }

        StartTodoSegmentPulse(null);
        TodoPanelSegments.Visibility = Visibility.Collapsed;
        TodoPanelBar.Visibility = Visibility.Visible;
        UiMotion.FadeBrush(
            TodoPanelBarFill,
            Border.BackgroundProperty,
            ThemeBrush("RuAccentBrush"));
        AnimateTodoBarFill(TodoBarWidth * state.BarFraction);
    }

    private static Brush? TodoSegmentBrush(TodoSegmentState segment) => segment switch
    {
        TodoSegmentState.Done => ThemeBrush("RuSuccessBrush"),
        TodoSegmentState.Active => ThemeBrush("RuAccentBrush"),
        _ => ThemeBrush("RuLineBrush"),
    };

    private void EnsureTodoSegments(int count)
    {
        if (_todoSegmentVisuals.Count == count && TodoPanelSegments.Children.Count == count)
        {
            return;
        }

        StopTodoSegmentPulse();
        TodoPanelSegments.Children.Clear();
        _todoSegmentVisuals.Clear();
        for (int index = 0; index < count; index++)
        {
            var segment = new Border
            {
                Width = TodoSegmentWidth,
                Height = TodoSegmentHeight,
                CornerRadius = UiCornerRadius.MicroPill,
            };
            _todoSegmentVisuals.Add(segment);
            TodoPanelSegments.Children.Add(segment);
        }
    }

    private void StartTodoSegmentPulse(Border? active)
    {
        if (ReferenceEquals(active, _todoActiveSegment))
        {
            return;
        }

        _todoSegmentPulse?.Stop();
        _todoSegmentPulse = null;
        if (_todoActiveSegment is { } previous)
        {
            previous.Opacity = 1;
        }

        _todoActiveSegment = active;
        if (active is null || !UiTiming.AnimationsEnabled)
        {
            if (active is not null)
            {
                active.Opacity = 1;
            }

            return;
        }

        try
        {
            var pulse = new DoubleAnimation
            {
                From = 1,
                To = 0.55,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(pulse, active);
            Storyboard.SetTargetProperty(pulse, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(pulse);
            _todoSegmentPulse = storyboard;
            storyboard.Begin();
        }
        catch (Exception)
        {
            active.Opacity = 1;
        }
    }

    private void StopTodoSegmentPulse()
    {
        _todoSegmentPulse?.Stop();
        _todoSegmentPulse = null;
        if (_todoActiveSegment is { } active)
        {
            active.Opacity = 1;
            _todoActiveSegment = null;
        }
    }

    private void AnimateTodoBarFill(double target)
    {
        _todoBarStoryboard?.Stop();
        _todoBarStoryboard = null;
        double current = double.IsNaN(TodoPanelBarFill.Width) ? 0 : TodoPanelBarFill.Width;
        if (!UiTiming.AnimationsEnabled || Math.Abs(current - target) < 0.5)
        {
            TodoPanelBarFill.Width = target;
            return;
        }

        try
        {
            var grow = new DoubleAnimation
            {
                From = current,
                To = target,
                Duration = UiTiming.TodoListOpen,
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, TodoPanelBarFill);
            Storyboard.SetTargetProperty(grow, "Width");
            var storyboard = new Storyboard();
            storyboard.Children.Add(grow);
            storyboard.Completed += (_, _) => TodoPanelBarFill.Width = target;
            _todoBarStoryboard = storyboard;
            storyboard.Begin();
        }
        catch (Exception)
        {
            TodoPanelBarFill.Width = target;
        }
    }

    /// <summary>
    /// Opens or closes the list drawer with a height and opacity transition. The
    /// current rendered height is captured before an interrupted storyboard stops,
    /// so a quick second click reverses from where the drawer actually is.
    /// </summary>
    private void ApplyTodoListState(bool open)
    {
        bool changed = open != _todoListOpen;
        _todoListOpen = open;
        double target = _todoListTargetHeight;
        double currentHeight = TodoPanelScroll.Visibility == Visibility.Visible
            ? TodoPanelScroll.ActualHeight
            : 0;

        if (!open && !changed && TodoPanelScroll.Visibility == Visibility.Collapsed)
        {
            return;
        }

        if (!changed && open && Math.Abs(currentHeight - target) < 0.5)
        {
            TodoPanelScroll.Height = target;
            return;
        }

        _todoListStoryboard?.Stop();
        _todoListStoryboard = null;
        TodoPanelScroll.Height = currentHeight;
        TodoPanelList.Visibility = Visibility.Visible;
        TodoPanelScroll.Visibility = Visibility.Visible;

        if (!UiTiming.AnimationsEnabled)
        {
            FinishTodoListState(open);
            return;
        }

        double fromOpacity = TodoPanelScroll.Opacity;
        if (open && changed)
        {
            // A fresh drawer starts from nothing; a viewport resize while open keeps
            // the list visible and only adjusts its height.
            TodoPanelScroll.Opacity = 0;
            fromOpacity = 0;
        }

        double toHeight = open ? target : 0;
        double toOpacity = open ? 1 : 0;
        if (Math.Abs(currentHeight - toHeight) < 0.5 && Math.Abs(fromOpacity - toOpacity) < 0.01)
        {
            FinishTodoListState(open);
            return;
        }

        try
        {
            var height = new DoubleAnimation
            {
                From = currentHeight,
                To = toHeight,
                Duration = open ? UiTiming.TodoListOpen : UiTiming.TodoListClose,
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            var opacity = new DoubleAnimation
            {
                From = fromOpacity,
                To = toOpacity,
                Duration = open ? UiTiming.TodoListOpen : UiTiming.TodoListClose,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(height, TodoPanelScroll);
            Storyboard.SetTargetProperty(height, "Height");
            Storyboard.SetTarget(opacity, TodoPanelScroll);
            Storyboard.SetTargetProperty(opacity, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(height);
            storyboard.Children.Add(opacity);
            storyboard.Completed += (_, _) =>
            {
                _todoListStoryboard = null;
                FinishTodoListState(open);
            };
            _todoListStoryboard = storyboard;
            storyboard.Begin();
        }
        catch (Exception)
        {
            FinishTodoListState(open);
        }
    }

    private void FinishTodoListState(bool open)
    {
        TodoPanelScroll.Height = open ? _todoListTargetHeight : 0;
        TodoPanelScroll.Opacity = 1;
        TodoPanelList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelScroll.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double EstimateTodoListHeight(int count, double maxHeight)
    {
        if (count <= 0)
        {
            return 0;
        }

        double content = count * TodoRowHeightEstimate
            + Math.Max(0, count - 1) * TodoRowGapEstimate
            + TodoListPaddingEstimate;
        return Math.Min(maxHeight, content);
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private bool IsPromptCardShown => RunPanel.Approval is not null || RunPanel.Question is not null;

    /// <summary>Hides the shelf while a prompt card is up and brings it back afterwards.</summary>
    private void ApplyTodoShelfForPromptCard()
    {
        if (TodoPanel is null) return;
        if (IsPromptCardShown) UiMotion.SetVisible(TodoPanel, false);
        else if (_latestTodos is not null) UpdateTodoPanel(_latestTodos);
    }

    private void ClearTodoPanel()
    {
        _latestTodos = null;
        _todoPanelExpanded = false;
        _todoListOpen = false;
        _todoListTargetHeight = 0;

        StopTodoMotion();
        TodoPanelToggleIcon.Glyph = "\uE70E";
        TodoPanelToggleIcon.Visibility = Visibility.Visible;
        TodoPanelHeader.Text = string.Empty;
        TodoPanelNext.Text = string.Empty;
        TodoPanelNext.Visibility = Visibility.Collapsed;
        TodoPanelNext.Opacity = 1;
        TodoPanelCount.Text = string.Empty;
        TodoPanelCount.Opacity = 1;
        TodoPanelMarkDot.Opacity = 0;
        TodoPanelMarkRing.Opacity = 1;
        TodoPanelSegments.Children.Clear();
        _todoSegmentVisuals.Clear();
        StopTodoSegmentPulse();
        TodoPanelSegments.Visibility = Visibility.Collapsed;
        TodoPanelBar.Visibility = Visibility.Collapsed;
        TodoPanelBarFill.Width = 0;
        TodoPanelBarFill.Background = ThemeBrush("RuAccentBrush");
        TodoPanelList.Render(null);
        TodoPanelList.Visibility = Visibility.Collapsed;
        TodoPanelScroll.Height = 0;
        TodoPanelScroll.Opacity = 1;
        TodoPanelScroll.Visibility = Visibility.Collapsed;
        UiMotion.SetVisible(TodoPanel, false);
    }

    private void StopTodoMotion()
    {
        _todoListStoryboard?.Stop();
        _todoListStoryboard = null;
        _todoBarStoryboard?.Stop();
        _todoBarStoryboard = null;
        StopTodoSegmentPulse();
    }

    private void OnTodoPanelToggleClick(object sender, RoutedEventArgs e)
    {
        _todoPanelExpanded = !_todoPanelExpanded;
        UpdateTodoPanel(_latestTodos);
    }
}
