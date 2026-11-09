using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private const double TodoSegmentWidth = 8;
    private const double TodoSegmentHeight = 3;
    private const double TodoBarWidth = 74;

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

        if (state.AllDone)
        {
            // A finished plan has nothing to expand; its summary stays until the next turn.
            _todoPanelExpanded = false;
        }

        bool listOpen = _todoPanelExpanded && !state.AllDone;
        TodoPanelCount.Text = $"{state.Done}/{state.Total}";
        TodoPanelHeader.Text = state.HeaderPrimary;
        // The collapsed shelf is one line; the next task stays available in the header
        // tooltip so folding the list does not hide it entirely.
        string headerTooltip = string.IsNullOrEmpty(state.HeaderSecondary)
            ? state.HeaderPrimary
            : $"{state.HeaderPrimary} — {state.HeaderSecondary}";
        ToolTipService.SetToolTip(TodoPanelHeader, headerTooltip);
        TodoPanelNext.Text = state.HeaderSecondary;
        TodoPanelNext.Visibility = listOpen && !string.IsNullOrEmpty(state.HeaderSecondary)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolTipService.SetToolTip(TodoPanelNext, state.HeaderSecondary);
        ApplyTodoMark(state);
        ApplyTodoProgress(state);
        TodoPanelScroll.MaxHeight = state.ListHeight;

        TodoPanelScroll.Visibility = listOpen ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelList.Visibility = TodoPanelScroll.Visibility;
        // The list opens above the line, so the chevron points the way it will open.
        TodoPanelToggleIcon.Glyph = listOpen ? "\uE70D" : "\uE70E";
        TodoPanelToggleIcon.Visibility = state.AllDone ? Visibility.Collapsed : Visibility.Visible;
        // A completed plan is a summary: pointer hover and click do nothing, but the
        // header stays reachable by Tab so a screen reader hears the completion line.
        TodoPanelToggle.IsHitTestVisible = !state.AllDone;
        string toggleName = state.AllDone
            ? "Task list complete"
            : listOpen ? "Collapse task list" : "Expand task list";
        AutomationProperties.SetName(TodoPanelToggle, toggleName);
        ToolTipService.SetToolTip(TodoPanelToggle, toggleName);
        if (listOpen)
        {
            TodoPanelList.Render(todos);
        }

        // An approval or question card floats over the composer and the shelf; the shelf
        // yields while one is up instead of showing its header above the card's edge.
        UiMotion.SetVisible(TodoPanel, !IsPromptCardShown);
    }

    private void ApplyTodoMark(AgentTodoPanelPresentation state)
    {
        // A finished plan shows the check; otherwise the active dot or the pending ring.
        TodoPanelMarkCheck.Visibility = state.AllDone ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelMarkDot.Visibility = state.HasActive ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelMarkRing.Visibility = !state.HasActive && !state.AllDone
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyTodoProgress(AgentTodoPanelPresentation state)
    {
        AutomationProperties.SetName(
            state.UseSegments ? TodoPanelSegments : TodoPanelBar,
            $"Tasks {state.Done} of {state.Total} done");

        TodoPanelSegments.Children.Clear();
        if (state.UseSegments)
        {
            foreach (TodoSegmentState segment in state.Segments)
            {
                TodoPanelSegments.Children.Add(new Border
                {
                    Width = TodoSegmentWidth,
                    Height = TodoSegmentHeight,
                    CornerRadius = UiCornerRadius.MicroPill,
                    Background = ThemeBrush(segment switch
                    {
                        TodoSegmentState.Done => "RuSuccessBrush",
                        TodoSegmentState.Active => "RuAccentBrush",
                        _ => "RuLineBrush",
                    }),
                });
            }
        }

        TodoPanelSegments.Visibility = state.UseSegments ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelBar.Visibility = state.UseSegments ? Visibility.Collapsed : Visibility.Visible;
        TodoPanelBarFill.Width = TodoBarWidth * state.BarFraction;
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

    /// <summary>A finished plan keeps its completion summary until the next turn starts.</summary>
    private void ClearCompletedTodoShelf()
    {
        if (_latestTodos is { Count: > 0 } todos && todos.All(todo => todo.IsCompleted))
        {
            ClearTodoPanel();
        }
    }

    private void ClearTodoPanel()
    {
        _latestTodos = null;
        _todoPanelExpanded = false;
        TodoPanelToggleIcon.Glyph = "\uE70E";
        TodoPanelToggleIcon.Visibility = Visibility.Visible;
        TodoPanelToggle.IsHitTestVisible = true;
        TodoPanelHeader.Text = string.Empty;
        TodoPanelNext.Text = string.Empty;
        TodoPanelNext.Visibility = Visibility.Collapsed;
        TodoPanelCount.Text = string.Empty;
        TodoPanelMarkDot.Visibility = Visibility.Collapsed;
        TodoPanelMarkRing.Visibility = Visibility.Visible;
        TodoPanelMarkCheck.Visibility = Visibility.Collapsed;
        TodoPanelSegments.Children.Clear();
        TodoPanelSegments.Visibility = Visibility.Collapsed;
        TodoPanelBar.Visibility = Visibility.Collapsed;
        TodoPanelBarFill.Width = 0;
        TodoPanelList.Render(null);
        TodoPanelScroll.Visibility = Visibility.Collapsed;
        UiMotion.SetVisible(TodoPanel, false);
    }

    private void OnTodoPanelToggleClick(object sender, RoutedEventArgs e)
    {
        if (_latestTodos is { Count: > 0 } todos && todos.All(todo => todo.IsCompleted))
        {
            return;
        }

        _todoPanelExpanded = !_todoPanelExpanded;
        UpdateTodoPanel(_latestTodos);
    }
}
