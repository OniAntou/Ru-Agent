using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    // Driven by chronological activity data, never by recycled visual rows.
    private void UpdateTodoPanel(IReadOnlyList<TodoItemModel>? todos)
    {
        _latestTodos = todos;
        var state = AgentTodoPanelPresentation.Create(todos, RootGrid.ActualHeight);
        if (!state.IsVisible)
        {
            ClearTodoPanel();
            return;
        }

        TodoPanelCount.Text = $"{state.Done}/{state.Total}";
        TodoPanelHeader.Text = state.Current;
        ToolTipService.SetToolTip(TodoPanelHeader, state.Current);
        TodoPanelProgress.Maximum = state.Total;
        TodoPanelProgress.Value = state.Done;
        AutomationProperties.SetName(TodoPanelProgress, $"Tasks {state.Done} of {state.Total} done");
        TodoPanelScroll.MaxHeight = state.ListHeight;
        // Filled accent dot while a task is in progress; a muted ring while only
        // pending tasks remain.
        TodoPanelMark.Fill = state.HasActive ? ThemeBrush("RuAccentBrush") : null;
        TodoPanelMark.Stroke = state.HasActive ? null : ThemeBrush("RuMutedBrush");
        TodoPanelScroll.Visibility = _todoPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        TodoPanelList.Visibility = TodoPanelScroll.Visibility;
        // The list opens above the line, so the chevron points the way it will open.
        TodoPanelToggleIcon.Glyph = _todoPanelExpanded ? "\uE70D" : "\uE70E";
        AutomationProperties.SetName(TodoPanelToggle, _todoPanelExpanded ? "Collapse task list" : "Expand task list");
        ToolTipService.SetToolTip(TodoPanelToggle, _todoPanelExpanded ? "Collapse task list" : "Expand task list");
        if (_todoPanelExpanded) TodoPanelList.Render(todos);
        // An approval or question card floats over the composer and the shelf; the shelf
        // yields while one is up instead of showing its header above the card's edge.
        UiMotion.SetVisible(TodoPanel, !IsPromptCardShown);
    }

    private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Microsoft.UI.Xaml.Media.Brush : null;

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
        TodoPanelToggleIcon.Glyph = "\uE70E";
        TodoPanelList.Render(null);
        TodoPanelScroll.Visibility = Visibility.Collapsed;
        UiMotion.SetVisible(TodoPanel, false);
    }

    private void OnTodoPanelToggleClick(object sender, RoutedEventArgs e)
    {
        _todoPanelExpanded = !_todoPanelExpanded;
        UpdateTodoPanel(_latestTodos);
    }
}
